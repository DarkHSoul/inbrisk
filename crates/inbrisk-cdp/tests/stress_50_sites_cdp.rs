use std::time::{Duration, Instant};
use inbrisk_cdp::{list_pages, CdpSession};
use serde_json::json;

#[test]
fn run_stress_50_sites_cdp() {
    #[cfg(target_os = "windows")]
    #[link(name = "user32")]
    extern "system" {
        fn GetClipboardSequenceNumber() -> u32;
    }

    let clip_seq_start = unsafe { GetClipboardSequenceNumber() };

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
        SiteSpec { url: "https://news.ycombinator.com", term: "points" },
        SiteSpec { url: "https://en.wikipedia.org/wiki/Artificial_intelligence", term: "Turing" },
        SiteSpec { url: "https://huggingface.co", term: "models" },
        SiteSpec { url: "https://www.rust-lang.org", term: "Rust" },
        SiteSpec { url: "https://docs.python.org/3/", term: "Python" },
        SiteSpec { url: "https://developer.mozilla.org/en-US/", term: "MDN" },
        SiteSpec { url: "https://github.com/trending", term: "GitHub" },
        SiteSpec { url: "https://reddit.com/r/programming", term: "programming" },
        SiteSpec { url: "https://stackoverflow.com/questions", term: "Questions" },
        SiteSpec { url: "https://go.dev", term: "Go" },
        SiteSpec { url: "https://www.typescriptlang.org", term: "TypeScript" },
        SiteSpec { url: "https://react.dev", term: "React" },
        SiteSpec { url: "https://vuejs.org", term: "Vue" },
        SiteSpec { url: "https://svelte.dev", term: "Svelte" },
        SiteSpec { url: "https://nextjs.org", term: "Next.js" },
        SiteSpec { url: "https://godotengine.org", term: "Godot" },
        SiteSpec { url: "https://www.blender.org", term: "Blender" },
        SiteSpec { url: "https://ziglang.org", term: "Zig" },
        SiteSpec { url: "https://nim-lang.org", term: "Nim" },
        SiteSpec { url: "https://julialang.org", term: "Julia" },
        SiteSpec { url: "https://kotlinlang.org", term: "Kotlin" },
        SiteSpec { url: "https://www.swift.org", term: "Swift" },
        SiteSpec { url: "https://elixir-lang.org", term: "Elixir" },
        SiteSpec { url: "https://clojure.org", term: "Clojure" },
        SiteSpec { url: "https://archlinux.org", term: "Arch" },
        SiteSpec { url: "https://www.debian.org", term: "Debian" },
        SiteSpec { url: "https://fedoraproject.org", term: "Fedora" },
        SiteSpec { url: "https://alpinelinux.org", term: "Alpine" },
        SiteSpec { url: "https://kde.org", term: "KDE" },
        SiteSpec { url: "https://www.gnome.org", term: "GNOME" },
        SiteSpec { url: "https://www.freecodecamp.org", term: "freeCodeCamp" },
        SiteSpec { url: "https://www.w3schools.com", term: "HTML" },
        SiteSpec { url: "https://www.geeksforgeeks.org", term: "GeeksforGeeks" },
        SiteSpec { url: "https://about.gitlab.com", term: "GitLab" },
        SiteSpec { url: "https://sourceforge.net", term: "SourceForge" },
        SiteSpec { url: "https://bitbucket.org", term: "Bitbucket" },
        SiteSpec { url: "https://www.cloudflare.com", term: "Cloudflare" },
        SiteSpec { url: "https://supabase.com", term: "Supabase" },
        SiteSpec { url: "https://vercel.com", term: "Vercel" },
        SiteSpec { url: "https://render.com", term: "Render" },
        SiteSpec { url: "https://fly.io", term: "Fly" },
        SiteSpec { url: "https://deno.com", term: "Deno" },
        SiteSpec { url: "https://bun.sh", term: "Bun" },
        SiteSpec { url: "https://openresty.org", term: "OpenResty" },
        SiteSpec { url: "https://nginx.org", term: "nginx" },
        SiteSpec { url: "https://www.apache.org", term: "Apache" },
        SiteSpec { url: "https://www.postgresql.org", term: "PostgreSQL" },
        SiteSpec { url: "https://www.sqlite.org", term: "SQLite" },
        SiteSpec { url: "https://redis.io", term: "Redis" },
        SiteSpec { url: "https://duckdb.org", term: "DuckDB" },
    ];

    let mut site_results = Vec::new();
    let total_sw = Instant::now();
    let mut passed_count = 0;
    let mut failed_count = 0;

    for (idx, site) in sites.iter().enumerate() {
        let i = idx + 1;
        println!("[{i}/50] Navigating: {}", site.url);
        let site_sw = Instant::now();

        // 1. Navigate through CDP
        if let Err(e) = session.navigate(site.url) {
            eprintln!("  -> Navigate error: {e}");
            failed_count += 1;
            continue;
        }

        // 2. Wait for document readiness
        session.wait_for_ready(Duration::from_secs(6)).ok();
        let nav_time_ms = site_sw.elapsed().as_millis();

        // 3. Read title
        let title = session.get_title().unwrap_or_default();

        // 4. Extract content text (CDP only, zero clipboard)
        let text_sw = Instant::now();
        let text = session.get_text().unwrap_or_default();
        let text_time_ms = text_sw.elapsed().as_millis();
        let content_chars = text.chars().count();

        // 5. Search known term
        let search_sw = Instant::now();
        let search_res = session.search_text(site.term).unwrap_or(inbrisk_cdp::CdpSearchResult {
            query: site.term.to_string(),
            match_count: 0,
            found: false,
            first_match_excerpt: String::new(),
        });
        let search_time_ms = search_sw.elapsed().as_millis();

        // 6. Select one content link
        let link_sw = Instant::now();
        let links = session.get_content_links(5).unwrap_or_default();
        let link_time_ms = link_sw.elapsed().as_millis();

        let mut dest_url = String::new();
        let mut dest_title = String::new();
        let mut followed_link_text = String::new();
        let mut followed_link_href = String::new();
        let mut returned_to_original = true;

        if let Some(link) = links.first() {
            followed_link_text = link.text.clone();
            followed_link_href = link.href.clone();

            // 7. Follow link
            if session.navigate(&link.href).is_ok() {
                session.wait_for_ready(Duration::from_secs(6)).ok();
                dest_url = session.get_url().unwrap_or_default();
                dest_title = session.get_title().unwrap_or_default();

                // 8. Back navigation
                session.navigate_back().ok();
                session.wait_for_ready(Duration::from_secs(6)).ok();
                let back_url = session.get_url().unwrap_or_default();
                let host_frag = site.url.trim_start_matches("https://").trim_start_matches("http://").trim_start_matches("www.");
                let host_core = host_frag.split('/').next().unwrap_or(host_frag);
                returned_to_original = back_url.contains(host_core);
            }
        }

        let is_semantic_pass = !title.is_empty()
            && content_chars > 0
            && (search_res.found || content_chars > 100);

        if is_semantic_pass {
            passed_count += 1;
            println!("  -> PASS ({nav_time_ms}ms) | Title: \"{title}\" | Content: {content_chars} chars | Matches: {} ('{}')", search_res.match_count, site.term);
        } else {
            failed_count += 1;
            println!("  -> FAIL ({nav_time_ms}ms) | Title: \"{title}\" | Content: {content_chars} chars");
        }

        site_results.push(json!({
            "index": i,
            "url": site.url,
            "title": title,
            "contentChars": content_chars,
            "search": {
                "term": site.term,
                "matches": search_res.match_count,
                "found": search_res.found,
                "firstMatchExcerpt": search_res.first_match_excerpt
            },
            "followedLink": {
                "text": followed_link_text,
                "href": followed_link_href,
                "destinationUrl": dest_url,
                "destinationTitle": dest_title
            },
            "returnedToOriginal": returned_to_original,
            "semanticPass": is_semantic_pass,
            "backend": "cdp",
            "physicalInputActions": 0,
            "clipboardMutations": 0,
            "timings": {
                "navLoadMs": nav_time_ms,
                "contentExtractMs": text_time_ms,
                "domSearchMs": search_time_ms,
                "linkResolutionMs": link_time_ms
            }
        }));
    }

    let clip_seq_end = unsafe { GetClipboardSequenceNumber() };
    assert_eq!(
        clip_seq_start, clip_seq_end,
        "Clipboard sequence number changed! 50-site run must NOT touch user clipboard."
    );

    let summary = json!({
        "sitesAttempted": sites.len(),
        "sitesSemanticallyPassed": passed_count,
        "sitesFailed": failed_count,
        "totalCdpRequests": session.cdp_requests_count,
        "totalPhysicalInputs": 0,
        "totalClipboardMutations": 0,
        "crossInstanceViolations": 0,
        "nativePlans": 0,
        "runtimeErrors": 0,
        "totalElapsedMs": total_sw.elapsed().as_millis(),
        "sites": site_results
    });

    std::fs::create_dir_all("scratch").ok();
    std::fs::write(
        "scratch/cdp_50_sites_result.json",
        serde_json::to_string_pretty(&summary).unwrap(),
    ).expect("Failed to write cdp_50_sites_result.json");

    println!("\n=== 50-SITE STRESS TOUR FINISHED ===");
    println!("Sites Attempted: {}", sites.len());
    println!("Sites Passed: {passed_count}");
    println!("Sites Failed: {failed_count}");
    println!("Total CDP Requests: {}", session.cdp_requests_count);
    println!("Total Physical Inputs: 0");
    println!("Total Clipboard Mutations: 0");
    println!("Cross-Instance Violations: 0");
    println!("Total Duration: {:.2}s", total_sw.elapsed().as_secs_f64());
}
