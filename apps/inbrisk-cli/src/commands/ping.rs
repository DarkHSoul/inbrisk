//! `ping`: round-trip latency of the shared-memory channel.
//!
//! This is the number that decides whether the CLI fast path is worth it, so it
//! is reported as a distribution (min/p50/p95/max) rather than an average.

use inbrisk_core::{ErrorCode, InbriskError};

use crate::cli::PingArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &PingArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;

    let mut samples: Vec<u64> = Vec::with_capacity(args.count);
    let mut first_error: Option<InbriskError> = None;
    for _ in 0..args.count {
        match runtime.ping() {
            Ok(micros) => samples.push(micros),
            Err(error) => {
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
    }

    if samples.is_empty() {
        let error = first_error.unwrap_or_else(|| {
            InbriskError::new(ErrorCode::Internal, "no ping round trip completed")
        });
        return Err(CliError::Runtime(error));
    }

    let mut sorted = samples.clone();
    sorted.sort_unstable();
    let min_us = sorted.first().copied().unwrap_or(0);
    let max_us = sorted.last().copied().unwrap_or(0);
    let p50_us = percentile(&sorted, 50);
    let p95_us = percentile(&sorted, 95);
    let mean_us = samples.iter().sum::<u64>() / samples.len() as u64;
    let failed = args.count - samples.len();

    if args.json {
        crate::emit_json!(serde_json::json!({
            "count": args.count,
            "ok": samples.len(),
            "failed": failed,
            "min_us": min_us,
            "p50_us": p50_us,
            "p95_us": p95_us,
            "max_us": max_us,
            "mean_us": mean_us,
            "samples_us": samples,
        }))?;
        return Ok(EXIT_OK);
    }

    let mut line = format!(
        "ping {}/{} ok  min={}  p50={}  p95={}  max={}  mean={}",
        samples.len(),
        args.count,
        output::fmt_us(min_us),
        output::fmt_us(p50_us),
        output::fmt_us(p95_us),
        output::fmt_us(max_us),
        output::fmt_us(mean_us)
    );
    if failed > 0 {
        line.push_str(&format!("  failed={failed}"));
    }
    output::print_line(&line)?;
    Ok(EXIT_OK)
}

/// Nearest-rank percentile over an ascending slice.
fn percentile(sorted: &[u64], percent: u32) -> u64 {
    if sorted.is_empty() {
        return 0;
    }
    let rank = ((percent as f64 / 100.0) * sorted.len() as f64).ceil() as usize;
    let index = rank.saturating_sub(1).min(sorted.len() - 1);
    sorted[index]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percentiles_use_the_nearest_rank() {
        let sorted = [10, 20, 30, 40];
        assert_eq!(percentile(&sorted, 50), 20);
        assert_eq!(percentile(&sorted, 95), 40);
        assert_eq!(percentile(&sorted, 100), 40);
        assert_eq!(percentile(&[], 50), 0);
    }

    #[test]
    fn twenty_samples_put_p95_near_the_top() {
        let sorted: Vec<u64> = (1..=20).collect();
        assert_eq!(percentile(&sorted, 50), 10);
        assert_eq!(percentile(&sorted, 95), 19);
        assert_eq!(percentile(&sorted, 100), 20);
    }
}
