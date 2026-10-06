//! One COM/UIA apartment.
//!
//! An apartment is created and destroyed *inside* a worker thread; nothing
//! COM-shaped ever crosses a thread or a process boundary. What leaves the
//! apartment is plain `ElementData`.

use inbrisk_core::{ErrorCode, Hwnd, InbriskError, Rect, Result};
use inbrisk_protocol::selector::{MatchPolicy, Selector};
use windows::core::{Interface, BSTR};
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::CLSCTX_ALL;
use windows::Win32::System::Variant::{VARIANT, VARIANT_0, VARIANT_0_0, VARIANT_0_0_0, VT_I4};
use windows::Win32::UI::Accessibility::{
    CUIAutomation, IUIAutomation, IUIAutomationElement, IUIAutomationElementArray,
    IUIAutomationExpandCollapsePattern, IUIAutomationInvokePattern, IUIAutomationScrollPattern,
    IUIAutomationSelectionItemPattern, IUIAutomationTogglePattern, IUIAutomationValuePattern,
    ScrollAmount_LargeDecrement, ScrollAmount_LargeIncrement, ScrollAmount_NoAmount,
    ScrollAmount_SmallDecrement, ScrollAmount_SmallIncrement, ToggleState_On, TreeScope_Children,
    TreeScope_Descendants, UIA_ControlTypePropertyId, UIA_ExpandCollapsePatternId,
    UIA_InvokePatternId, UIA_ScrollPatternId, UIA_SelectionItemPatternId, UIA_TogglePatternId,
    UIA_ValuePatternId, UIA_ValueValuePropertyId,
};

use inbrisk_protocol::action::{ScrollDirection, SelectOption};

use crate::control_type::{control_type_id, has_value, role_name};
use crate::runtime_id::runtime_id_of;

/// Resolve an export of ole32.dll at runtime. Static linking to ole32 aborts
/// process start (STATUS_DLL_INIT_FAILED) on some hosts.
unsafe fn ole32_proc(name: windows::core::PCSTR) -> Option<unsafe extern "system" fn() -> isize> {
    use windows::core::s;
    use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryA};
    let module = LoadLibraryA(s!("ole32.dll")).ok()?;
    GetProcAddress(module, name)
}

/// Ensure COM is initialized on the current thread.
pub fn ensure_com() {
    unsafe {
        use windows::core::s;
        type CoInitializeExFn = unsafe extern "system" fn(*const core::ffi::c_void, u32) -> i32;
        if let Some(p) = ole32_proc(s!("CoInitializeEx")) {
            let f: CoInitializeExFn = std::mem::transmute(p);
            // COINIT_MULTITHREADED = 0. RPC_E_CHANGED_MODE simply means the
            // thread is already an STA; UIA works either way for reads.
            let _ = f(std::ptr::null(), 0);
        }
    }
}

/// Dynamic equivalent of `CoCreateInstance`.
unsafe fn co_create_instance<T: Interface>(
    clsid: &windows::core::GUID,
    ctx: u32,
) -> windows::core::Result<T> {
    use windows::core::s;
    type Fn_ = unsafe extern "system" fn(
        *const windows::core::GUID,
        *mut core::ffi::c_void,
        u32,
        *const windows::core::GUID,
        *mut *mut core::ffi::c_void,
    ) -> windows::core::HRESULT;
    let p = ole32_proc(s!("CoCreateInstance"))
        .ok_or_else(|| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;
    let f: Fn_ = std::mem::transmute(p);
    let mut out = std::ptr::null_mut();
    f(clsid, std::ptr::null_mut(), ctx, &T::IID, &mut out).ok()?;
    Ok(T::from_raw(out))
}

/// Plain, `Send` snapshot of a UIA element.
#[derive(Debug, Clone)]
pub struct ElementData {
    pub hwnd: Hwnd,
    pub process_id: u32,
    pub runtime_id: Vec<i32>,
    pub control_type_id: i32,
    pub role: String,
    pub name: String,
    pub automation_id: String,
    pub class_name: String,
    pub bounds: Rect,
    pub enabled: bool,
    pub offscreen: bool,
    pub value: Option<String>,
    pub patterns: Vec<String>,
}

impl ElementData {
    pub fn to_element_ref(
        &self,
        id: u64,
        generation: u64,
        backend: &str,
    ) -> inbrisk_core::ElementRef {
        inbrisk_core::ElementRef {
            id,
            hwnd: self.hwnd,
            process_id: self.process_id,
            generation,
            runtime_id: self.runtime_id.clone(),
            role: self.role.clone(),
            name: self.name.clone(),
            automation_id: self.automation_id.clone(),
            class_name: self.class_name.clone(),
            bounds: self.bounds,
            enabled: self.enabled,
            offscreen: self.offscreen,
            backend: backend.to_string(),
        }
    }

    pub fn label(&self) -> String {
        if !self.name.is_empty() {
            format!("{} \"{}\"", self.role, self.name)
        } else if !self.automation_id.is_empty() {
            format!("{} #{}", self.role, self.automation_id)
        } else {
            self.role.clone()
        }
    }
}

/// A COM apartment bound to one worker thread.
pub struct UiaApartment {
    automation: IUIAutomation,
}

impl std::fmt::Debug for UiaApartment {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("UiaApartment")
    }
}

impl UiaApartment {
    pub fn open() -> Result<Self> {
        ensure_com();
        unsafe {
            let automation: IUIAutomation = co_create_instance(&CUIAutomation, CLSCTX_ALL.0)
                .map_err(|e| {
                    InbriskError::internal(format!("CUIAutomation could not be created: {e}"))
                })?;
            Ok(Self { automation })
        }
    }

    pub fn automation(&self) -> &IUIAutomation {
        &self.automation
    }

    pub fn root(&self, hwnd: Hwnd) -> Result<IUIAutomationElement> {
        unsafe {
            self.automation
                .ElementFromHandle(HWND(hwnd.0 as *mut core::ffi::c_void))
                .map_err(|e| {
                    InbriskError::not_found(format!("UIA cannot see window {hwnd}: {e}"))
                        .with_hint("the window may have closed; re-observe")
                })
        }
    }

    pub fn focused(&self) -> Result<IUIAutomationElement> {
        unsafe {
            self.automation
                .GetFocusedElement()
                .map_err(|e| InbriskError::internal(format!("GetFocusedElement failed: {e}")))
        }
    }

    pub fn focused_element(&self) -> Option<ElementData> {
        let el = self.focused().ok()?;
        self.describe(&el).ok()
    }

    fn build_condition(
        &self,
        selector: &Selector,
    ) -> Result<windows::Win32::UI::Accessibility::IUIAutomationCondition> {
        unsafe {
            let mut condition = self
                .automation
                .CreateTrueCondition()
                .map_err(|e| InbriskError::internal(format!("CreateTrueCondition: {e}")))?;

            if let Some(role) = &selector.role {
                if let Some(id) = control_type_id(role) {
                    let var = variant_i32(id);
                    let c = self
                        .automation
                        .CreatePropertyCondition(UIA_ControlTypePropertyId, &var)
                        .map_err(|e| {
                            InbriskError::internal(format!("CreatePropertyCondition: {e}"))
                        })?;
                    condition = self
                        .automation
                        .CreateAndCondition(&condition, &c)
                        .map_err(|e| InbriskError::internal(format!("CreateAndCondition: {e}")))?;
                } else {
                    return Err(InbriskError::invalid_plan(format!("unknown role '{role}'"))
                        .with_hint("use a UIA control type such as button, edit, document"));
                }
            }
            Ok(condition)
        }
    }

    /// Find elements under a window. Returns `(matches, scanned)`.
    pub fn find(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
        all: bool,
        limit: usize,
    ) -> Result<(Vec<ElementData>, u32)> {
        let (pairs, scanned) = self.find_match_pairs(hwnd, selector, all, limit)?;
        Ok((pairs.into_iter().map(|(_, data)| data).collect(), scanned))
    }

    /// Optimized single-pass element search that pairs COM elements with metadata.
    pub fn find_match_pairs(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
        all: bool,
        limit: usize,
    ) -> Result<(Vec<(IUIAutomationElement, ElementData)>, u32)> {
        let root = self.root(hwnd)?;
        let condition = self.build_condition(selector)?;
        let array: IUIAutomationElementArray = unsafe {
            root.FindAll(TreeScope_Descendants, &condition)
                .map_err(|e| InbriskError::internal(format!("FindAll failed: {e}")))?
        };
        let length = unsafe { array.Length().unwrap_or(0) }.max(0) as usize;

        let mut matches: Vec<(IUIAutomationElement, ElementData)> = Vec::new();
        let mut scanned = 0u32;
        let mut candidates: Vec<String> = Vec::new();
        let need_value = selector.text.is_some();

        // The root itself can be the thing being looked for (document, window).
        if let Ok(mut root_data) = self.describe_shallow(&root, need_value) {
            scanned += 1;
            if matches_selector(&root_data, selector) {
                if root_data.value.is_none() {
                    root_data.value = self.read_value(&root);
                }
                root_data.patterns = self.patterns_of(&root);
                matches.push((root.clone(), root_data));
            }
        }

        for i in 0..length {
            let Ok(el) = (unsafe { array.GetElement(i as i32) }) else {
                continue;
            };
            let Ok(mut data) = self.describe_shallow(&el, need_value) else {
                continue;
            };
            scanned += 1;
            if matches_selector(&data, selector) {
                if matches.len() < 24 {
                    candidates.push(data.name.clone());
                }
                if data.value.is_none() {
                    data.value = self.read_value(&el);
                }
                data.patterns = self.patterns_of(&el);
                matches.push((el, data));
                if !all && matches.len() > 1 && selector.policy == MatchPolicy::Strict {
                    if matches.len() > 8 {
                        break;
                    }
                }
                if all && matches.len() >= limit {
                    break;
                }
            }
        }

        if !all {
            match matches.len() {
                0 => return Ok((Vec::new(), scanned)),
                1 => {}
                _ => {
                    if selector.policy == MatchPolicy::Strict && selector.index.is_none() {
                        return Err(InbriskError::new(
                            ErrorCode::CloseMatchesFound,
                            format!(
                                "{} elements match {}: {}",
                                matches.len(),
                                selector.describe(),
                                candidates.join(", ")
                            ),
                        )
                        .with_hint("add automation_id/within, or set index/policy"));
                    }
                    let idx = selector.index.unwrap_or(0).min(matches.len() - 1);
                    let chosen = matches.swap_remove(idx);
                    return Ok((vec![chosen], scanned));
                }
            }
        }

        if let Some(idx) = selector.index {
            if matches.is_empty() {
                return Ok((Vec::new(), scanned));
            }
            let chosen = matches.swap_remove(idx.min(matches.len() - 1));
            return Ok((vec![chosen], scanned));
        }

        Ok((matches, scanned))
    }

    /// Read every interesting property of one element.
    pub fn describe(&self, el: &IUIAutomationElement) -> Result<ElementData> {
        let mut data = self.describe_shallow(el, true)?;
        data.patterns = self.patterns_of(el);
        Ok(data)
    }

    /// Read shallow properties needed for filtering without expensive pattern queries.
    pub fn describe_shallow(&self, el: &IUIAutomationElement, need_value: bool) -> Result<ElementData> {
        unsafe {
            let control_type_id = el.CurrentControlType().map(|v| v.0).unwrap_or(0);
            let name = el.CurrentName().map(bstr).unwrap_or_default();
            let automation_id = el.CurrentAutomationId().map(bstr).unwrap_or_default();
            let class_name = el.CurrentClassName().map(bstr).unwrap_or_default();
            let process_id = el.CurrentProcessId().unwrap_or(0).max(0) as u32;
            let enabled = el.CurrentIsEnabled().map(|b| b.as_bool()).unwrap_or(true);
            let offscreen = el
                .CurrentIsOffscreen()
                .map(|b| b.as_bool())
                .unwrap_or(false);
            let rect = el.CurrentBoundingRectangle().unwrap_or_default();
            let native = el.CurrentNativeWindowHandle().ok();
            let runtime_id = runtime_id_of(el);
            let value = if need_value { self.read_value(el) } else { None };

            Ok(ElementData {
                hwnd: native.map(|h| Hwnd(h.0 as isize)).unwrap_or(Hwnd(0)),
                process_id,
                runtime_id,
                control_type_id,
                role: role_name(control_type_id).to_string(),
                name,
                automation_id,
                class_name,
                bounds: Rect::new(
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                ),
                enabled,
                offscreen,
                value,
                patterns: Vec::new(),
            })
        }
    }

    fn read_value(&self, el: &IUIAutomationElement) -> Option<String> {
        unsafe {
            if let Ok(prop) = el.GetCurrentPropertyValue(UIA_ValueValuePropertyId) {
                if let Ok(s) = prop_to_string(&prop) {
                    if !s.is_empty() {
                        return Some(s);
                    }
                }
            }
            let pattern: IUIAutomationValuePattern =
                el.GetCurrentPatternAs(UIA_ValuePatternId).ok()?;
            let value = pattern.CurrentValue().ok()?;
            Some(bstr(value))
        }
    }

    fn patterns_of(&self, el: &IUIAutomationElement) -> Vec<String> {
        let mut out = Vec::new();
        unsafe {
            if el
                .GetCurrentPatternAs::<IUIAutomationInvokePattern>(UIA_InvokePatternId)
                .is_ok()
            {
                out.push("invoke".into());
            }
            if el
                .GetCurrentPatternAs::<IUIAutomationValuePattern>(UIA_ValuePatternId)
                .is_ok()
            {
                out.push("value".into());
            }
            if el
                .GetCurrentPatternAs::<IUIAutomationTogglePattern>(UIA_TogglePatternId)
                .is_ok()
            {
                out.push("toggle".into());
            }
            if el
                .GetCurrentPatternAs::<IUIAutomationSelectionItemPattern>(
                    UIA_SelectionItemPatternId,
                )
                .is_ok()
            {
                out.push("selection_item".into());
            }
            if el
                .GetCurrentPatternAs::<IUIAutomationExpandCollapsePattern>(
                    UIA_ExpandCollapsePatternId,
                )
                .is_ok()
            {
                out.push("expand_collapse".into());
            }
        }
        out
    }

    /// `Invoke` through the semantic pattern — never a synthesized click.
    pub fn invoke(&self, hwnd: Hwnd, selector: &Selector) -> Result<ElementData> {
        let (mut matches, _) = self.find_match_pairs(hwnd, selector, false, 1)?;
        let (el, target) = matches.pop().ok_or_else(|| not_found(selector))?;
        if !target.enabled {
            return Err(InbriskError::element_disabled(format!(
                "{} is disabled",
                target.label()
            )));
        }
        unsafe {
            let pattern: IUIAutomationInvokePattern =
                el.GetCurrentPatternAs(UIA_InvokePatternId).map_err(|_| {
                    InbriskError::new(
                        ErrorCode::PatternUnavailable,
                        format!("{} does not support InvokePattern", target.role),
                    )
                    .with_hint("fall back to click for this element")
                })?;
            pattern
                .Invoke()
                .map_err(|e| InbriskError::internal(format!("Invoke failed: {e}")))?;
        }
        Ok(target)
    }

    /// Set a value through `ValuePattern` — no keystrokes.
    pub fn set_value(&self, hwnd: Hwnd, selector: &Selector, value: &str) -> Result<ElementData> {
        let (mut matches, _) = self.find_match_pairs(hwnd, selector, false, 1)?;
        let (el, mut target) = matches.pop().ok_or_else(|| not_found(selector))?;
        if !target.enabled {
            return Err(InbriskError::element_disabled(format!(
                "{} is disabled",
                target.label()
            )));
        }
        unsafe {
            let pattern: IUIAutomationValuePattern =
                el.GetCurrentPatternAs(UIA_ValuePatternId).map_err(|_| {
                    InbriskError::new(
                        ErrorCode::PatternUnavailable,
                        format!("{} does not support ValuePattern", target.role),
                    )
                    .with_hint("use the type action for this control")
                })?;
            let bstr = BSTR::from(value);
            pattern
                .SetValue(&bstr)
                .map_err(|e| InbriskError::internal(format!("SetValue failed: {e}")))?;
            if let Ok(curr) = pattern.CurrentValue() {
                target.value = Some(curr.to_string());
            }
        }
        Ok(target)
    }

    pub fn focus(&self, hwnd: Hwnd, selector: &Selector) -> Result<ElementData> {
        let (mut matches, _) = self.find_match_pairs(hwnd, selector, false, 1)?;
        let (el, target) = matches.pop().ok_or_else(|| not_found(selector))?;
        unsafe {
            el.SetFocus()
                .map_err(|e| InbriskError::internal(format!("SetFocus failed: {e}")))?;
        }
        Ok(target)
    }

    pub fn select_item(&self, hwnd: Hwnd, selector: &Selector) -> Result<ElementData> {
        let (mut matches, _) = self.find_match_pairs(hwnd, selector, false, 1)?;
        let (el, target) = matches.pop().ok_or_else(|| not_found(selector))?;
        if !target.enabled {
            return Err(InbriskError::element_disabled(format!(
                "{} is disabled",
                target.label()
            )));
        }
        unsafe {
            let pattern: IUIAutomationSelectionItemPattern = el
                .GetCurrentPatternAs(UIA_SelectionItemPatternId)
                .map_err(|_| {
                    InbriskError::new(
                        ErrorCode::PatternUnavailable,
                        "element does not support SelectionItemPattern",
                    )
                })?;
            pattern
                .Select()
                .map_err(|e| InbriskError::internal(format!("Select failed: {e}")))?;
        }
        Ok(target)
    }

    pub fn expand_collapse(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
        expand: bool,
    ) -> Result<ElementData> {
        let (mut matches, _) = self.find_match_pairs(hwnd, selector, false, 1)?;
        let (el, target) = matches.pop().ok_or_else(|| not_found(selector))?;
        if !target.enabled {
            return Err(InbriskError::element_disabled(format!(
                "{} is disabled",
                target.label()
            )));
        }
        unsafe {
            let pattern: IUIAutomationExpandCollapsePattern = el
                .GetCurrentPatternAs(UIA_ExpandCollapsePatternId)
                .map_err(|_| {
                    InbriskError::new(
                        ErrorCode::PatternUnavailable,
                        "element does not support ExpandCollapsePattern",
                    )
                })?;
            let r = if expand {
                pattern.Expand()
            } else {
                pattern.Collapse()
            };
            r.map_err(|e| InbriskError::internal(format!("ExpandCollapse failed: {e}")))?;
        }
        Ok(target)
    }

    /// Resolve a selector to a live COM element (inside this apartment).
    pub fn element_for(&self, hwnd: Hwnd, selector: &Selector) -> Result<IUIAutomationElement> {
        let root = self.root(hwnd)?;
        let condition = self.build_condition(selector)?;
        let array: IUIAutomationElementArray = unsafe {
            root.FindAll(TreeScope_Descendants, &condition)
                .map_err(|e| InbriskError::internal(format!("FindAll failed: {e}")))?
        };
        let length = unsafe { array.Length().unwrap_or(0) }.max(0) as usize;
        let wanted_pid = None::<u32>;
        let mut found: Option<IUIAutomationElement> = None;
        let mut count = 0usize;
        for i in 0..length {
            let Ok(el) = (unsafe { array.GetElement(i as i32) }) else {
                continue;
            };
            let Ok(data) = self.describe(&el) else {
                continue;
            };
            let _ = wanted_pid;
            if matches_selector(&data, selector) {
                count += 1;
                if found.is_none() {
                    found = Some(el.clone());
                }
            }
        }
        found.ok_or_else(|| not_found(selector)).map(|el| {
            let _ = count;
            el
        })
    }
}

fn not_found(selector: &Selector) -> InbriskError {
    InbriskError::not_found(format!("no element matches {}", selector.describe()))
        .with_hint("observe the window first; the tree may still be loading")
}

/// Lenient variant: never fails on ambiguity, used by probes and waits.
fn lenient(selector: &Selector) -> Selector {
    let mut s = selector.clone();
    s.policy = MatchPolicy::Best;
    s
}

impl UiaApartment {
    /// Resolve exactly one element (strict about ambiguity).
    pub fn find_one(&self, hwnd: Hwnd, selector: &Selector) -> Result<ElementData> {
        let (matches, _) = self.find(hwnd, selector, false, 1)?;
        matches
            .into_iter()
            .next()
            .ok_or_else(|| not_found(selector))
    }

    /// Current value of an element, if it publishes one.
    pub fn read_value_of(&self, hwnd: Hwnd, selector: &Selector) -> Result<Option<String>> {
        let (mut matches, _) = self.find_match_pairs(hwnd, &lenient(selector), false, 1)?;
        let (el, _) = matches.pop().ok_or_else(|| not_found(selector))?;
        Ok(self.read_value(&el))
    }

    /// `(exists, enabled, offscreen)` for wait probes.
    pub fn element_state(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
    ) -> Result<Option<(bool, bool, bool)>> {
        let (matches, _) = self.find(hwnd, &lenient(selector), true, 1)?;
        Ok(matches.first().map(|d| (true, d.enabled, d.offscreen)))
    }

    /// Scroll through `ScrollPattern`; `false` means "no pattern, use input".
    pub fn scroll_element(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
        direction: ScrollDirection,
        amount: i32,
    ) -> Result<bool> {
        let (mut matches, _) = self.find_match_pairs(hwnd, &lenient(selector), false, 1)?;
        let (el, _) = matches.pop().ok_or_else(|| not_found(selector))?;
        let pattern: IUIAutomationScrollPattern =
            match unsafe { el.GetCurrentPatternAs(UIA_ScrollPatternId) } {
                Ok(p) => p,
                Err(_) => return Ok(false),
            };
        // More than three notches is treated as a page-ish scroll.
        let step = if amount >= 3 {
            match direction {
                ScrollDirection::Up | ScrollDirection::Left => ScrollAmount_LargeDecrement,
                ScrollDirection::Down | ScrollDirection::Right => ScrollAmount_LargeIncrement,
            }
        } else {
            match direction {
                ScrollDirection::Up | ScrollDirection::Left => ScrollAmount_SmallDecrement,
                ScrollDirection::Down | ScrollDirection::Right => ScrollAmount_SmallIncrement,
            }
        };
        let no = ScrollAmount_NoAmount;
        let (horizontal, vertical) = match direction {
            ScrollDirection::Left => (step, no),
            ScrollDirection::Right => (step, no),
            ScrollDirection::Up => (no, step),
            ScrollDirection::Down => (no, step),
        };
        unsafe {
            pattern
                .Scroll(horizontal, vertical)
                .map_err(|e| InbriskError::internal(format!("ScrollPattern.Scroll failed: {e}")))?;
        }
        Ok(true)
    }

    /// Select an option inside a list/combo box.
    pub fn select_option(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
        option: &SelectOption,
    ) -> Result<ElementData> {
        let base = lenient(selector);
        match option {
            SelectOption::Text(text) => {
                let mut child = Selector::default();
                child.within = Some(Box::new(base));
                child.name = Some(text.clone());
                child.policy = MatchPolicy::Best;
                self.select_item(hwnd, &child)
            }
            SelectOption::Index(index) => {
                let mut probe = Selector::default();
                probe.within = Some(Box::new(base.clone()));
                probe.role = Some("listitem".into());
                probe.policy = MatchPolicy::Best;
                let (matches, _) = self.find(hwnd, &probe, true, 64)?;
                let target = matches
                    .get(*index)
                    .ok_or_else(|| {
                        InbriskError::not_found(format!(
                            "list has {} items; index {index} is out of range",
                            matches.len()
                        ))
                    })?
                    .clone();
                let mut exact = Selector::default();
                if !target.automation_id.is_empty() {
                    exact.automation_id = Some(target.automation_id.clone());
                } else {
                    exact.name = Some(target.name.clone());
                    exact.exact = Some(true);
                }
                exact.within = Some(Box::new(base));
                self.select_item(hwnd, &exact)
            }
        }
    }

    /// Toggle a check box, optionally driving it to a desired state.
    pub fn toggle(
        &self,
        hwnd: Hwnd,
        selector: &Selector,
        desired: Option<bool>,
    ) -> Result<ElementData> {
        let lenient_selector = lenient(selector);
        let (mut matches, _) = self.find_match_pairs(hwnd, &lenient_selector, false, 1)?;
        let (el, target) = matches.pop().ok_or_else(|| not_found(selector))?;
        if !target.enabled {
            return Err(InbriskError::element_disabled(format!(
                "{} is disabled",
                target.label()
            )));
        }
        unsafe {
            let pattern: IUIAutomationTogglePattern =
                el.GetCurrentPatternAs(UIA_TogglePatternId).map_err(|_| {
                    InbriskError::new(
                        ErrorCode::PatternUnavailable,
                        "element does not support TogglePattern",
                    )
                })?;
            if let Some(want) = desired {
                let current = pattern.CurrentToggleState().unwrap_or(ToggleState_On);
                if (current == ToggleState_On) == want {
                    return Ok(target);
                }
            }
            pattern
                .Toggle()
                .map_err(|e| InbriskError::internal(format!("Toggle failed: {e}")))?;
        }
        Ok(target)
    }

    /// Breadth-first dump of a window's interaction tree.
    ///
    /// Bounded on purpose: `max_depth` and `max_elements` are hard limits, so a
    /// pathological provider can never turn `observe` into an unbounded walk.
    pub fn tree(
        &self,
        hwnd: Hwnd,
        max_depth: u32,
        max_elements: usize,
    ) -> Result<Vec<ElementData>> {
        let root = self.root(hwnd)?;
        let condition = unsafe {
            self.automation
                .CreateTrueCondition()
                .map_err(|e| InbriskError::internal(format!("CreateTrueCondition: {e}")))?
        };
        let mut out: Vec<ElementData> = Vec::new();
        let mut frontier: Vec<(IUIAutomationElement, u32)> = vec![(root, 1)];
        while let Some((el, depth)) = frontier.pop() {
            if out.len() >= max_elements || depth > max_depth {
                continue;
            }
            if let Ok(data) = self.describe(&el) {
                out.push(data);
            }
            let children: IUIAutomationElementArray =
                match unsafe { el.FindAll(TreeScope_Children, &condition) } {
                    Ok(c) => c,
                    Err(_) => continue,
                };
            let length = unsafe { children.Length().unwrap_or(0) }.max(0) as usize;
            for i in (0..length).rev() {
                if frontier.len() + out.len() >= max_elements {
                    break;
                }
                if let Ok(child) = unsafe { children.GetElement(i as i32) } {
                    frontier.push((child, depth + 1));
                }
            }
        }
        Ok(out)
    }
}

fn bstr(v: BSTR) -> String {
    v.to_string()
}

/// `VT_I4` variant, built field by field so no union assignment is needed.
fn variant_i32(value: i32) -> VARIANT {
    let inner = VARIANT_0_0 {
        vt: VT_I4,
        wReserved1: 0,
        wReserved2: 0,
        wReserved3: 0,
        Anonymous: VARIANT_0_0_0 { lVal: value },
    };
    VARIANT {
        Anonymous: VARIANT_0 {
            Anonymous: std::mem::ManuallyDrop::new(inner),
        },
    }
}

fn prop_to_string(var: &VARIANT) -> windows::core::Result<String> {
    unsafe {
        let inner = &*var.Anonymous.Anonymous;
        if inner.vt == windows::Win32::System::Variant::VT_BSTR {
            let b = &*inner.Anonymous.bstrVal;
            return Ok(b.to_string());
        }
        Ok(String::new())
    }
}

/// Post-filter: property conditions can only express equality, so all the
/// substring/exact/regex logic lives here.
pub fn matches_selector(data: &ElementData, selector: &Selector) -> bool {
    if let Some(role) = &selector.role {
        if let Some(id) = control_type_id(role) {
            if data.control_type_id != id {
                return false;
            }
        }
    }
    if let Some(automation_id) = &selector.automation_id {
        if &data.automation_id != automation_id {
            return false;
        }
    }
    if let Some(class_name) = &selector.class_name {
        if !data.class_name.eq_ignore_ascii_case(class_name) {
            return false;
        }
    }
    if let Some(process) = &selector.process {
        // Compared by the caller against the process name; here we only have a
        // pid, so a numeric selector is honoured directly.
        if let Ok(pid) = process.parse::<u32>() {
            if data.process_id != pid {
                return false;
            }
        }
    }
    // `selector.hwnd` is the search root, already applied by ElementFromHandle.
    // Child controls such as Notepad's RichEdit have their own native hwnd, so
    // requiring equality here rejects the element the caller asked for.
    if let Some(name) = &selector.name {
        let exact = selector.exact.unwrap_or(false);
        if exact {
            if !data.name.eq_ignore_ascii_case(name) {
                return false;
            }
        } else if !contains_ci(&data.name, name) {
            return false;
        }
    }
    if let Some(pattern) = &selector.name_regex {
        match regex::Regex::new(pattern) {
            Ok(re) => {
                if !re.is_match(&data.name) {
                    return false;
                }
            }
            Err(_) => return false,
        }
    }
    if let Some(text) = &selector.text {
        let haystack = format!("{} {}", data.name, data.value.clone().unwrap_or_default());
        if !contains_ci(&haystack, text) {
            return false;
        }
    }
    true
}

fn contains_ci(haystack: &str, needle: &str) -> bool {
    if needle.is_empty() {
        return true;
    }
    haystack.to_lowercase().contains(&needle.to_lowercase())
}

/// `has_value` is re-exported for callers that need to know whether a role
/// carries text; keeps the vocabulary in one place.
pub fn value_capable(control_type_id: i32) -> bool {
    has_value(control_type_id)
}

#[allow(dead_code)]
fn unused_interface() {
    // Keeps the `Interface` import meaningful for pattern casts.
    fn _assert<T: Interface>() {}
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn search_root_hwnd_does_not_reject_a_child_document() {
        let data = ElementData {
            hwnd: Hwnd(67542),
            process_id: 19048,
            runtime_id: Vec::new(),
            control_type_id: 50030,
            role: "document".into(),
            name: "Metin düzenleyici".into(),
            automation_id: String::new(),
            class_name: "RichEditD2DPT".into(),
            bounds: Rect::new(0, 0, 10, 10),
            enabled: true,
            offscreen: false,
            value: None,
            patterns: Vec::new(),
        };
        let mut selector = Selector::role("document");
        selector.hwnd = Some(Hwnd(133094));
        assert!(matches_selector(&data, &selector));
    }
}
