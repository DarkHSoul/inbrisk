//! UIA execution service: a concurrent read pool and a serialized mutation
//! lane.
//!
//! This is the architecture the previous generation of Inbrisk converged on,
//! ported as-is:
//!
//! ```text
//! UIA Read Pool            Mutation Lane
//! ├─ worker 1 (MTA)        │
//! ├─ worker 2 (MTA)        └─ exactly one thread, serialized
//! └─ worker N (MTA)
//! ```
//!
//! * Reads are concurrent, bounded and time-limited. A provider that stops
//!   answering cannot wedge the whole runtime: the caller times out and the
//!   stuck worker is taken out of rotation and replaced.
//! * Mutations are serialized, so `Invoke`/`SetValue` never race each other
//!   inside the same tree.
//! * We never go back to "one global COM thread".

use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use crossbeam_channel::{bounded, RecvTimeoutError, Sender, TrySendError};
use inbrisk_core::{log_warn, now_ms, ErrorCode, InbriskError, Result};
use parking_lot::Mutex;

use crate::apartment::UiaApartment;

type Job = Box<dyn FnOnce(&UiaApartment) + Send + 'static>;

const QUEUE_DEPTH: usize = 4;

#[derive(Debug, Clone, Default)]
pub struct UiaStats {
    pub reads: u64,
    pub mutations: u64,
    pub read_timeouts: u64,
    pub mutation_timeouts: u64,
    pub poisoned_workers: u64,
    pub worker_jobs: Vec<u64>,
    pub readers: usize,
}

struct Worker {
    tx: Sender<Job>,
    busy_since_ms: Arc<AtomicU64>,
    jobs: Arc<AtomicU64>,
    retired: Arc<AtomicBool>,
}

/// Concurrent, bounded read pool.
pub struct ReadPool {
    workers: Mutex<Vec<Worker>>,
    next: AtomicUsize,
    reads: AtomicU64,
    timeouts: AtomicU64,
    poisoned: AtomicU64,
    default_timeout: Duration,
    /// A hung provider must not consume a worker forever.
    poison_after: Duration,
}

impl std::fmt::Debug for ReadPool {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ReadPool")
            .field("reads", &self.reads.load(Ordering::Relaxed))
            .finish()
    }
}

impl ReadPool {
    pub fn new(readers: usize, default_timeout: Duration) -> Result<Self> {
        let pool = Self {
            workers: Mutex::new(Vec::with_capacity(readers)),
            next: AtomicUsize::new(0),
            reads: AtomicU64::new(0),
            timeouts: AtomicU64::new(0),
            poisoned: AtomicU64::new(0),
            default_timeout,
            poison_after: Duration::from_secs(10),
        };
        for i in 0..readers.max(1) {
            let w = spawn_worker(i)?;
            pool.workers.lock().push(w);
        }
        Ok(pool)
    }

    /// Run a read job on a pool worker.
    pub fn submit<R, F>(&self, timeout: Option<Duration>, f: F) -> Result<R>
    where
        R: Send + 'static,
        F: FnOnce(&UiaApartment) -> R + Send + 'static,
    {
        let (tx, rx) = bounded(1);
        let job: Job = Box::new(move |ap| {
            let _ = tx.send(f(ap));
        });

        self.dispatch(job)?;
        let timeout = timeout.unwrap_or(self.default_timeout);
        match rx.recv_timeout(timeout) {
            Ok(v) => {
                self.reads.fetch_add(1, Ordering::Relaxed);
                Ok(v)
            }
            Err(RecvTimeoutError::Timeout) => {
                self.timeouts.fetch_add(1, Ordering::Relaxed);
                Err(
                    InbriskError::new(ErrorCode::Timeout, format!("UIA read exceeded {timeout:?}"))
                        .with_hint(
                            "the provider is not answering; retry or use a narrower selector",
                        ),
                )
            }
            Err(RecvTimeoutError::Disconnected) => Err(InbriskError::internal(
                "UIA read worker died before answering",
            )),
        }
    }

    fn dispatch(&self, job: Job) -> Result<()> {
        let mut workers = self.workers.lock();
        self.reap_poisoned(&mut workers)?;
        let count = workers.len();
        if count == 0 {
            return Err(InbriskError::internal("no UIA read workers available"));
        }
        let start = self.next.fetch_add(1, Ordering::Relaxed) % count;
        let mut pending = Some(job);
        for offset in 0..count {
            let idx = (start + offset) % count;
            let this = match pending.take() {
                Some(j) => j,
                None => break,
            };
            match workers[idx].tx.try_send(this) {
                Ok(()) => return Ok(()),
                Err(TrySendError::Full(returned)) => {
                    pending = Some(returned);
                    continue;
                }
                Err(TrySendError::Disconnected(returned)) => {
                    pending = Some(returned);
                    continue;
                }
            }
        }
        Err(
            InbriskError::new(ErrorCode::BudgetExhausted, "every UIA read worker is busy")
                .with_hint("retry shortly; reads are bounded on purpose"),
        )
    }

    /// Retire workers that have been stuck on a provider for too long.
    fn reap_poisoned(&self, workers: &mut Vec<Worker>) -> Result<()> {
        let now = now_ms();
        let poison_ms = self.poison_after.as_millis() as u64;
        let mut replacement_needed = 0usize;
        workers.retain(|w| {
            let busy = w.busy_since_ms.load(Ordering::Acquire);
            let stuck = busy != 0 && now.saturating_sub(busy) > poison_ms;
            if stuck {
                log_warn!(
                    "uia",
                    "retiring a read worker stuck for {} ms; replacing it",
                    now.saturating_sub(busy)
                );
                w.retired.store(true, Ordering::Release);
                replacement_needed += 1;
                self.poisoned.fetch_add(1, Ordering::Relaxed);
                false
            } else {
                true
            }
        });
        for _ in 0..replacement_needed {
            let id = workers.len();
            workers.push(spawn_worker(id)?);
        }
        Ok(())
    }

    pub fn stats(&self) -> UiaStats {
        let workers = self.workers.lock();
        UiaStats {
            reads: self.reads.load(Ordering::Relaxed),
            mutations: 0,
            read_timeouts: self.timeouts.load(Ordering::Relaxed),
            mutation_timeouts: 0,
            poisoned_workers: self.poisoned.load(Ordering::Relaxed),
            worker_jobs: workers
                .iter()
                .map(|w| w.jobs.load(Ordering::Relaxed))
                .collect(),
            readers: workers.len(),
        }
    }
}

fn spawn_worker(id: usize) -> Result<Worker> {
    let (tx, rx) = bounded::<Job>(QUEUE_DEPTH);
    let busy_since_ms = Arc::new(AtomicU64::new(0));
    let jobs = Arc::new(AtomicU64::new(0));
    let retired = Arc::new(AtomicBool::new(false));

    let busy = Arc::clone(&busy_since_ms);
    let counter = Arc::clone(&jobs);
    std::thread::Builder::new()
        .name(format!("inbrisk-uia-read-{id}"))
        .spawn(move || {
            let apartment = match UiaApartment::open() {
                Ok(a) => a,
                Err(e) => {
                    log_warn!("uia", "read worker {id} could not open an apartment: {e}");
                    return;
                }
            };
            while let Ok(job) = rx.recv() {
                busy.store(now_ms(), Ordering::Release);
                job(&apartment);
                busy.store(0, Ordering::Release);
                counter.fetch_add(1, Ordering::Relaxed);
            }
        })
        .map_err(|e| InbriskError::internal(format!("cannot spawn UIA read worker: {e}")))?;

    Ok(Worker {
        tx,
        busy_since_ms,
        jobs,
        retired,
    })
}

/// Serialized mutation lane: exactly one thread, one apartment, one job at a
/// time.
pub struct MutationLane {
    tx: Sender<Job>,
    mutations: AtomicU64,
    timeouts: AtomicU64,
    busy_since_ms: Arc<AtomicU64>,
    default_timeout: Duration,
}

impl std::fmt::Debug for MutationLane {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MutationLane")
            .field("mutations", &self.mutations.load(Ordering::Relaxed))
            .finish()
    }
}

impl MutationLane {
    pub fn new(default_timeout: Duration) -> Result<Self> {
        let (tx, rx) = bounded::<Job>(32);
        let busy_since_ms = Arc::new(AtomicU64::new(0));
        let busy = Arc::clone(&busy_since_ms);
        std::thread::Builder::new()
            .name("inbrisk-uia-mutation".into())
            .spawn(move || {
                let apartment = match UiaApartment::open() {
                    Ok(a) => a,
                    Err(e) => {
                        log_warn!("uia", "mutation lane could not open an apartment: {e}");
                        return;
                    }
                };
                while let Ok(job) = rx.recv() {
                    busy.store(now_ms(), Ordering::Release);
                    job(&apartment);
                    busy.store(0, Ordering::Release);
                }
            })
            .map_err(|e| InbriskError::internal(format!("cannot spawn UIA mutation lane: {e}")))?;
        Ok(Self {
            tx,
            mutations: AtomicU64::new(0),
            timeouts: AtomicU64::new(0),
            busy_since_ms,
            default_timeout,
        })
    }

    pub fn submit<R, F>(&self, timeout: Option<Duration>, f: F) -> Result<R>
    where
        R: Send + 'static,
        F: FnOnce(&UiaApartment) -> R + Send + 'static,
    {
        let (tx, rx) = bounded(1);
        let job: Job = Box::new(move |ap| {
            let _ = tx.send(f(ap));
        });
        let started = Instant::now();
        let timeout = timeout.unwrap_or(self.default_timeout);
        self.tx
            .send_timeout(job, timeout)
            .map_err(|_| InbriskError::new(ErrorCode::Timeout, "mutation lane is backed up"))?;
        let remaining = timeout
            .saturating_sub(started.elapsed())
            .max(Duration::from_millis(1));
        match rx.recv_timeout(remaining) {
            Ok(v) => {
                self.mutations.fetch_add(1, Ordering::Relaxed);
                Ok(v)
            }
            Err(RecvTimeoutError::Timeout) => {
                self.timeouts.fetch_add(1, Ordering::Relaxed);
                Err(InbriskError::new(
                    ErrorCode::Timeout,
                    format!("UIA mutation exceeded {timeout:?}"),
                )
                .with_hint("the target provider is not responding"))
            }
            Err(RecvTimeoutError::Disconnected) => {
                Err(InbriskError::internal("UIA mutation lane died"))
            }
        }
    }

    pub fn is_busy(&self) -> bool {
        self.busy_since_ms.load(Ordering::Acquire) != 0
    }
}

/// The whole UIA service.
#[derive(Debug)]
pub struct UiaService {
    pub reads: ReadPool,
    pub mutations: MutationLane,
}

impl UiaService {
    /// `readers` read workers plus exactly one mutation lane.
    pub fn new(readers: usize, default_timeout: Duration) -> Result<Self> {
        Ok(Self {
            reads: ReadPool::new(readers, default_timeout)?,
            mutations: MutationLane::new(default_timeout)?,
        })
    }

    pub fn stats(&self) -> UiaStats {
        let mut s = self.reads.stats();
        s.mutations = self.mutations.mutations.load(Ordering::Relaxed);
        s.mutation_timeouts = self.mutations.timeouts.load(Ordering::Relaxed);
        s
    }
}
