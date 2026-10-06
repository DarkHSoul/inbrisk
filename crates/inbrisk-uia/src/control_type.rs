//! UIA control-type / property vocabulary.

/// Map a UIA control type id to the lowercase role name used by selectors.
pub fn role_name(id: i32) -> &'static str {
    match id {
        50000 => "button",
        50001 => "calendar",
        50002 => "checkbox",
        50003 => "combobox",
        50004 => "edit",
        50005 => "hyperlink",
        50006 => "image",
        50007 => "listitem",
        50008 => "list",
        50009 => "menu",
        50010 => "menubar",
        50011 => "menuitem",
        50012 => "progressbar",
        50013 => "radiobutton",
        50014 => "scrollbar",
        50015 => "slider",
        50016 => "spinner",
        50017 => "statusbar",
        50018 => "tab",
        50019 => "tabitem",
        50020 => "text",
        50021 => "toolbar",
        50022 => "tooltip",
        50023 => "tree",
        50024 => "treeitem",
        50025 => "custom",
        50026 => "group",
        50027 => "thumb",
        50028 => "datagrid",
        50029 => "dataitem",
        50030 => "document",
        50031 => "splitbutton",
        50032 => "window",
        50033 => "pane",
        50034 => "header",
        50035 => "headeritem",
        50036 => "table",
        50037 => "titlebar",
        50038 => "separator",
        50039 => "semanticzoom",
        50040 => "appbar",
        _ => "unknown",
    }
}

/// Reverse lookup, accepting a few friendly aliases.
pub fn control_type_id(role: &str) -> Option<i32> {
    let r = role.trim().to_ascii_lowercase();
    let id = match r.as_str() {
        "button" | "btn" => 50000,
        "calendar" => 50001,
        "checkbox" | "check" => 50002,
        "combobox" | "combo" | "dropdown" => 50003,
        "edit" | "textbox" | "input" | "textfield" => 50004,
        "hyperlink" | "link" => 50005,
        "image" | "img" => 50006,
        "listitem" => 50007,
        "list" => 50008,
        "menu" => 50009,
        "menubar" => 50010,
        "menuitem" => 50011,
        "progressbar" | "progress" => 50012,
        "radiobutton" | "radio" => 50013,
        "scrollbar" => 50014,
        "slider" => 50015,
        "spinner" => 50016,
        "statusbar" | "status" => 50017,
        "tab" => 50018,
        "tabitem" => 50019,
        "text" | "label" | "static" => 50020,
        "toolbar" => 50021,
        "tooltip" => 50022,
        "tree" => 50023,
        "treeitem" => 50024,
        "custom" => 50025,
        "group" => 50026,
        "thumb" => 50027,
        "datagrid" | "grid" => 50028,
        "dataitem" | "row" | "cell" => 50029,
        "document" | "doc" => 50030,
        "splitbutton" => 50031,
        "window" => 50032,
        "pane" | "panel" => 50033,
        "header" => 50034,
        "headeritem" | "columnheader" => 50035,
        "table" => 50036,
        "titlebar" => 50037,
        "separator" => 50038,
        _ => return None,
    };
    Some(id)
}

/// Control types whose `Value` property is worth reading.
pub fn has_value(id: i32) -> bool {
    matches!(id, 50004 | 50003 | 50015 | 50016 | 50030 | 50020)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roles_round_trip() {
        for role in [
            "button", "edit", "document", "menuitem", "listitem", "window", "pane", "tabitem",
        ] {
            let id = control_type_id(role).expect(role);
            assert_eq!(role_name(id), role);
        }
    }

    #[test]
    fn aliases_resolve() {
        assert_eq!(control_type_id("textbox"), Some(50004));
        assert_eq!(control_type_id("TextBox"), Some(50004));
        assert_eq!(role_name(50004), "edit");
        assert!(control_type_id("nonsense-role").is_none());
    }
}
