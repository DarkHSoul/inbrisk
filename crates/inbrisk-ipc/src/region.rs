//! The mapped region itself: create, open, map, unmap.

use inbrisk_core::{ErrorCode, InbriskError, Result};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::System::Memory::{
    CreateFileMappingW, MapViewOfFile, OpenFileMappingW, UnmapViewOfFile, FILE_MAP_ALL_ACCESS,
    MEMORY_MAPPED_VIEW_ADDRESS, PAGE_READWRITE,
};

use crate::layout::RegionView;
use crate::security::UserOnlyAttributes;

/// A live mapping of the Inbrisk shared region.
#[derive(Debug)]
pub struct MappedRegion {
    handle: HANDLE,
    view: MEMORY_MAPPED_VIEW_ADDRESS,
    size: usize,
    name: String,
    created: bool,
    region: RegionView,
}

unsafe impl Send for MappedRegion {}
unsafe impl Sync for MappedRegion {}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn process_alive(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }
    unsafe {
        use windows::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};
        match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(handle) => {
                let _ = CloseHandle(handle);
                true
            }
            Err(_) => false,
        }
    }
}

impl MappedRegion {
    /// Create the region. Only the runtime calls this.
    ///
    /// A second process must not reset a mapping whose owner is still alive.
    /// Doing that wipes the live runtime out from under its clients and leaves
    /// both processes looking hung.
    pub fn create(name: &str, size: usize) -> Result<Self> {
        if let Ok(existing) = Self::open(name) {
            let pid = existing.region.header().runtime_pid;
            if pid != 0 && pid != std::process::id() && process_alive(pid) {
                return Err(InbriskError::new(
                    ErrorCode::RuntimeUnavailable,
                    format!("Inbrisk is already running (pid {pid})"),
                )
                .with_hint("the running instance owns this session"));
            }
        }

        let name_w = wide(name);
        let attrs = UserOnlyAttributes::new();
        if let Some(a) = &attrs {
            a.touch();
        }
        let handle = unsafe {
            CreateFileMappingW(
                HANDLE::default(),
                attrs.as_ref().map(|a| a.as_ptr()),
                PAGE_READWRITE,
                (size as u64 >> 32) as u32,
                (size as u64 & 0xFFFF_FFFF) as u32,
                PCWSTR(name_w.as_ptr()),
            )
        }
        .map_err(|e| region_error(name, &e.to_string()))?;

        let view = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, size) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(region_error(name, "MapViewOfFile returned null"));
        }

        let region = unsafe { RegionView::new(view.Value as *mut u8, size) };
        Ok(Self {
            handle,
            view,
            size,
            name: name.to_string(),
            created: true,
            region,
        })
    }

    /// Open an existing region. Fails with `RuntimeUnavailable` when no runtime
    /// is listening.
    pub fn open(name: &str) -> Result<Self> {
        let name_w = wide(name);
        let handle =
            unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS.0, false, PCWSTR(name_w.as_ptr())) }
                .map_err(|err| {
                    InbriskError::new(
                        ErrorCode::RuntimeUnavailable,
                        format!("no Inbrisk runtime is listening on {name} ({err})"),
                    )
                    .with_hint("start inbrisk.exe (the runtime owns the region)")
                })?;

        let view = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, 0) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(region_error(name, "MapViewOfFile returned null"));
        }

        let magic = unsafe { *(view.Value as *const u64) };
        let size = if magic == crate::broker_channel::BROKER_IPC_MAGIC {
            crate::broker_channel::broker_layout().total_size
        } else {
            // The region size is not exposed by the mapping API; read it from the
            // header's layout fields once the view exists.
            let probe = unsafe {
                RegionView::new(
                    view.Value as *mut u8,
                    std::mem::size_of::<crate::layout::Header>(),
                )
            };
            let mut size = probe.header().header_size as usize;
            if size < crate::layout::HEADER_SIZE {
                size = crate::layout::HEADER_SIZE;
            }
            // Re-derive the true size from the layout description in the header.
            let total = total_size_from_header(probe.header());
            if total > size {
                size = total;
            }
            size
        };

        let region = unsafe { RegionView::new(view.Value as *mut u8, size) };
        Ok(Self {
            handle,
            view,
            size,
            name: name.to_string(),
            created: false,
            region,
        })
    }

    /// Open an existing region with an explicitly known size.
    pub fn open_sized(name: &str, size: usize) -> Result<Self> {
        let name_w = wide(name);
        let handle =
            unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS.0, false, PCWSTR(name_w.as_ptr())) }
                .map_err(|err| {
                    InbriskError::new(
                        ErrorCode::RuntimeUnavailable,
                        format!("cannot open shared region {name} ({err})"),
                    )
                })?;

        let view = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, 0) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(region_error(name, "MapViewOfFile returned null"));
        }

        let region = unsafe { RegionView::new(view.Value as *mut u8, size) };
        Ok(Self {
            handle,
            view,
            size,
            name: name.to_string(),
            created: false,
            region,
        })
    }

    pub fn region(&self) -> &RegionView {
        &self.region
    }

    pub fn size(&self) -> usize {
        self.size
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn is_creator(&self) -> bool {
        self.created
    }
}

/// Recompute the region size from the layout fields the creator published.
pub fn total_size_from_header(h: &crate::layout::Header) -> usize {
    let event_end = h.event_entries_offset + h.event_entries_size;
    let shard_end = if h.shard_stride > 0 {
        crate::layout::HEADER_SIZE as u64 + h.shard_stride * h.session_capacity as u64
    } else {
        crate::layout::HEADER_SIZE as u64
    };
    event_end.max(shard_end) as usize
}

impl Drop for MappedRegion {
    fn drop(&mut self) {
        unsafe {
            if !self.view.Value.is_null() {
                let _ = UnmapViewOfFile(self.view);
            }
            if !self.handle.is_invalid() {
                let _ = CloseHandle(self.handle);
            }
        }
    }
}

fn region_error(name: &str, detail: &str) -> InbriskError {
    InbriskError::new(
        ErrorCode::Internal,
        format!("shared region '{name}': {detail}"),
    )
}
