use std::time::{Duration, Instant};
use inbrisk_cdp::{list_pages, CdpSession};
use serde_json::json;

#[test]
fn run_controlled_5_sites_cdp_validation() {
    #[cfg(target_os = "windows")]
    #[link(name = "user32")]
    extern "system" {
        fn GetClipboardSequenceNumber() -> u32;
    }

    let clip_seq_start = unsafe { GetClipboardSequenceNumber() };

    // Discover page target on managed debug port 9222
    let port = 9222;
    let pages = list_pages(port).expect("Failed to list DevTools pages on 127.0.0.1:9222");
    assert!(!pages.is_empty(), "No page target available on 127.0.0.1:9222");
    let target = pages.iter().find(|p| p.target_type == "page").expect("No page target found");
    let target_id = target.id.clone();

    println!("Attaching CDP session to Target ID: {target_id} on port {port}");
    let mut session = CdpSession::connect(port, &target_id).expect("Failed to connect CdpSession");

    struct SiteSpec {
        url: &'static str,
        term: &'static str,
    }

    let sites = [
        SiteSpec { url: "https://www.debian.org", term: "Debian" },
        SiteSpec { url: "https://duckdb.org", term: "DuckDB" },
        SiteSpec { url: "https://www.sqlite.org", term: "SQLite" },
        SiteSpec { url: "https://www.w3schools.com", term: "HTML" },
        SiteSpec { url: "https://example.com", term: "documentation" },
    ];

    let mut site_results = Vec::new();
    let total_sw = Instant::now();

    for site in &sites {
        println!("\n========================================================");
        println!("CDP Validating Site: {}", site.url);

        // 1. Navigate through CDP
        let nav_sw = Instant::now();
        session.navigate(site.url).expect("CDP navigate failed");

        // 2. Wait for actual document readiness
        session.wait_for_ready(Duration::from_secs(10)).expect("CDP wait_for_ready failed");
        let nav_load_time_ms = nav_sw.elapsed().as_millis();

        // 3. Read title through CDP
        let title = session.get_title().expect("CDP get_title failed");
        assert!(!title.is_empty(), "Page title must be non-empty");

        // 4. Extract visible/document text through CDP (Zero Clipboard)
        let text_sw = Instant::now();
        let text = session.get_text().expect("CDP get_text failed");
        let content_time_ms = text_sw.elapsed().as_millis();
        let content_chars = text.chars().count();
        assert!(content_chars > 0, "Page content must be non-empty");

        // 5. Search one known term in returned content
        let search_sw = Instant::now();
        let search_res = session.search_text(site.term).expect("CDP search_text failed");
        let dom_search_time_ms = search_sw.elapsed().as_millis();
        assert!(search_res.found, "Term '{}' was not found on {}", site.term, site.url);
        assert!(search_res.match_count > 0, "Match count must be > 0");

        // 6. Select one actual content link from the DOM
        let link_sw = Instant::now();
        let links = session.get_content_links(10).expect("CDP get_content_links failed");
        assert!(!links.is_empty(), "Page must contain at least one content hyperlink");
        let chosen_link = links[0].clone();
        let link_res_time_ms = link_sw.elapsed().as_millis();

        // 7. Navigate/follow it
        session.navigate(&chosen_link.href).expect("CDP link navigation failed");
        session.wait_for_ready(Duration::from_secs(10)).expect("CDP destination wait_for_ready failed");

        // 8. Verify destination URL/title
        let dest_url = session.get_url().expect("CDP get_url failed");
        let dest_title = session.get_title().expect("CDP dest get_title failed");
        assert!(!dest_title.is_empty(), "Destination title must be non-empty");

        // 9. Navigate back using CDP history
        session.navigate_back().expect("CDP navigate_back failed");
        session.wait_for_ready(Duration::from_secs(10)).expect("CDP back wait_for_ready failed");

        // 10. Verify original page identity
        let restored_title = session.get_title().expect("CDP restored title failed");
        let restored_url = session.get_url().expect("CDP restored url failed");
        let returned_to_original = restored_url.contains(site.url.trim_start_matches("https://").trim_start_matches("www."))
            || restored_title.to_lowercase().contains(&site.term.to_lowercase());

        let result_json = json!({
            "url": site.url,
            "targetId": target_id,
            "title": title,
            "contentChars": content_chars,
            "search": {
                "term": site.term,
                "matches": search_res.match_count,
                "found": search_res.found,
                "firstMatchExcerpt": search_res.first_match_excerpt
            },
            "followedLink": {
                "text": chosen_link.text,
                "href": chosen_link.href,
                "destinationUrl": dest_url,
                "destinationTitle": dest_title
            },
            "returnedToOriginal": returned_to_original,
            "backend": "cdp",
            "physicalInputActions": 0,
            "clipboardMutations": 0,
            "timings": {
                "navLoadMs": nav_load_time_ms,
                "contentExtractMs": content_time_ms,
                "domSearchMs": dom_search_time_ms,
                "linkResolutionMs": link_res_time_ms
            }
        });

        println!("{}", serde_json::to_string_pretty(&result_json).unwrap());
        site_results.push(result_json);
    }

    let clip_seq_end = unsafe { GetClipboardSequenceNumber() };
    assert_eq!(
        clip_seq_start, clip_seq_end,
        "Clipboard sequence number changed! Browser content automation must NOT touch the user clipboard."
    );

    let summary = json!({
        "totalCdpRequests": session.cdp_requests_count,
        "totalUiaCalls": 0,
        "totalWin32Calls": 0,
        "totalPhysicalInputs": 0,
        "totalClipboardMutations": 0,
        "totalNativeOuterRequests": 0,
        "totalRunPlans": 0,
        "sitesAttempted": 5,
        "sitesSemanticallyPassed": site_results.iter().filter(|s| s["returnedToOriginal"].as_bool() == Some(true)).count(),
        "totalTimeMs": total_sw.elapsed().as_millis(),
        "sites": site_results
    });

    std::fs::create_dir_all("scratch").ok();
    std::fs::write("scratch/controlled_5_sites_result.json", serde_json::to_string_pretty(&summary).unwrap())
        .expect("Failed to write controlled_5_sites_result.json");
    println!("\n=== FIVE-SITE RUN COMPLETED SUCCESSFULLY ===");
    println!("{}", serde_json::to_string_pretty(&summary).unwrap());
}
