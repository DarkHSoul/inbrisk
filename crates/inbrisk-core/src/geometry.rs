use serde::{Deserialize, Serialize};

use crate::{Hwnd, InbriskError, Result};

/// Canonical coordinate spaces for physical and window-relative targeting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CoordinateSpace {
    /// Physical pixel coordinates in the Windows virtual desktop.
    VirtualScreenPhysical,

    /// Physical pixel coordinates relative to a target HWND client area.
    WindowClientPhysical,

    /// DPI-logical coordinates relative to a target HWND client area.
    WindowClientLogical,
}

/// Canonical point representation.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct Point {
    pub x: f64,
    pub y: f64,
    pub space: CoordinateSpace,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hwnd: Option<Hwnd>,
}

impl Point {
    pub const fn new(x: f64, y: f64, space: CoordinateSpace, hwnd: Option<Hwnd>) -> Self {
        Self { x, y, space, hwnd }
    }

    pub fn virtual_screen(x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            space: CoordinateSpace::VirtualScreenPhysical,
            hwnd: None,
        }
    }

    pub fn client_physical(hwnd: Hwnd, x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            space: CoordinateSpace::WindowClientPhysical,
            hwnd: Some(hwnd),
        }
    }

    pub fn client_logical(hwnd: Hwnd, x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            space: CoordinateSpace::WindowClientLogical,
            hwnd: Some(hwnd),
        }
    }

    /// Validate invariants for this coordinate point.
    pub fn validate(&self) -> Result<()> {
        match self.space {
            CoordinateSpace::VirtualScreenPhysical => {
                if self.hwnd.is_some() {
                    return Err(InbriskError::invalid_plan(
                        "VirtualScreenPhysical coordinate must not specify a target HWND",
                    ));
                }
            }
            CoordinateSpace::WindowClientPhysical | CoordinateSpace::WindowClientLogical => {
                match self.hwnd {
                    Some(h) if h.0 != 0 => {}
                    _ => {
                        return Err(InbriskError::invalid_plan(format!(
                            "{:?} coordinate requires a valid target HWND",
                            self.space
                        )));
                    }
                }
            }
        }
        Ok(())
    }
}

/// Screen-space rectangle in physical pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Rect {
    pub const fn new(x: i32, y: i32, width: i32, height: i32) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }

    pub const fn left(&self) -> i32 {
        self.x
    }

    pub const fn top(&self) -> i32 {
        self.y
    }

    pub const fn right(&self) -> i32 {
        self.x + self.width
    }

    pub const fn bottom(&self) -> i32 {
        self.y + self.height
    }

    pub const fn width(&self) -> i32 {
        self.width
    }

    pub const fn height(&self) -> i32 {
        self.height
    }

    pub fn is_empty(&self) -> bool {
        self.width <= 0 || self.height <= 0
    }

    pub fn center(&self) -> (i32, i32) {
        (self.x + self.width / 2, self.y + self.height / 2)
    }

    pub fn contains(&self, px: i32, py: i32) -> bool {
        px >= self.x && px < self.x + self.width && py >= self.y && py < self.y + self.height
    }

    pub fn contains_point(&self, p: &Point) -> bool {
        let px = p.x.round() as i32;
        let py = p.y.round() as i32;
        self.contains(px, py)
    }

    pub fn intersects(&self, other: &Rect) -> bool {
        !(other.x >= self.x + self.width
            || other.x + other.width <= self.x
            || other.y >= self.y + self.height
            || other.y + other.height <= self.y)
    }

    /// Distance from this rect to a point (0 when inside).
    pub fn distance_to(&self, px: i32, py: i32) -> f64 {
        let dx = (self.x - px).max(0).max(px - (self.x + self.width)) as f64;
        let dy = (self.y - py).max(0).max(py - (self.y + self.height)) as f64;
        (dx * dx + dy * dy).sqrt()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn point_validation_rules() {
        let vs = Point::virtual_screen(100.0, 200.0);
        assert!(vs.validate().is_ok());

        let vs_bad = Point {
            x: 100.0,
            y: 200.0,
            space: CoordinateSpace::VirtualScreenPhysical,
            hwnd: Some(Hwnd(1234)),
        };
        assert!(vs_bad.validate().is_err());

        let client = Point::client_physical(Hwnd(1234), 50.0, 50.0);
        assert!(client.validate().is_ok());

        let client_no_hwnd = Point {
            x: 50.0,
            y: 50.0,
            space: CoordinateSpace::WindowClientPhysical,
            hwnd: None,
        };
        assert!(client_no_hwnd.validate().is_err());

        let logical_no_hwnd = Point {
            x: 50.0,
            y: 50.0,
            space: CoordinateSpace::WindowClientLogical,
            hwnd: None,
        };
        assert!(logical_no_hwnd.validate().is_err());
    }

    #[test]
    fn rect_helpers_and_invariants() {
        let r = Rect::new(10, 20, 100, 50);
        assert_eq!(r.left(), 10);
        assert_eq!(r.top(), 20);
        assert_eq!(r.right(), 110);
        assert_eq!(r.bottom(), 70);
        assert_eq!(r.width(), 100);
        assert_eq!(r.height(), 50);
        assert_eq!(r.center(), (60, 45));
        assert!(r.right() >= r.left());
        assert!(r.bottom() >= r.top());
    }
}
