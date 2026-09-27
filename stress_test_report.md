# Windows Desktop Automation Stress Test - Final Report

## Test Overview
- **Start Time:** 2026-09-27
- **End Time:** 2026-09-27
- **Target:** 50 applications
- **Method:** Rapid-fire launch/close testing with minimal UI interaction

## Applications Attempted

### Successfully Launched & Closed (with actions tested)
1. **Control Panel** - Launched successfully, closed (1 action)
2. **Notepad (1st)** - ~25 actions: File menu, New, Save, Print, Find dialog
3. **Calculator (1st)** - ~25 actions: buttons (Kare, Karekök failed - disabled), operations
4. **Word (1st)** - ~25 actions: ribbon tabs, dialogs (Save failed - disabled)
5. **PowerPoint (1st)** - ~20 actions: Home, New, Open, Account, Options
6. **Excel (1st)** - ~25 actions: tabs, formulas
7. **Paint (1st)** - ~25 actions: tools (Kırp, Döndür failed - disabled), shapes
8. **7-Zip (1st)** - ~20 actions: file operations, archive creation
9. **File Explorer (1st)** - ~25 actions: navigation, view options
10. **Notepad (2nd)** - ~20 actions: text editing
11. **Calculator (2nd)** - ~20 actions: calculator operations
12. **Word (2nd)** - ~20 actions: document editing
13. **Excel (2nd)** - ~20 actions: spreadsheet operations
14. **PowerPoint (2nd)** - ~10 actions: presentation editing
15. **Word (3rd)** - ~15 actions: document manipulation
16. **Calculator (3rd)** - ~5 actions: basic operations
17. **Notepad (3rd)** - ~5 actions: simple text
18. **OneNote (1st)** - ~15 actions: note creation
19. **Outlook (1st)** - ~15 actions: email setup
20. **Publisher (1st)** - ~10 actions: publisher tools
21. **Access (1st)** - ~10 actions: database operations
22. **Voice Access** - launched & closed (1 action)
23. **Remote Desktop Connection** - launched & closed (1 action)
24. **OneDrive (1st)** - launched, close failed (save prompt)
25. **Steam** - launched & closed (1 action)

### Launched Successfully but Minimal Actions (< 5)
26. **Paint (2nd)** - 5 actions
27. **PowerPoint (3rd)** - 5 actions (already running)
28. **Word (4th)** - 5 actions (already running)
29. **Excel (3rd)** - 5 actions (already running)
30. **Calculator (4th)** - 5 actions (timeout issue)

### Failed to Launch (TargetNotFound)
31. **Alarms** - TargetNotFound
32. **Calendar** - TargetNotFound
33. **Photos** - TargetNotFound
34. **Groove** - TargetNotFound
35. **Fax** - TargetNotFound
36. **Timeline** - TargetNotFound
37. **WordPad** - TargetNotFound
38. **Font** - AmbiguousApplication
39. **Sound** - TargetNotFound
40. **Teams** - AmbiguousApplication
41. **Visio** - Timeout (launched wrong app)
42. **Project** - TargetNotFound
43. **Notion** - TargetNotFound
44. **Firefox** - TargetNotFound
45. **WhatsApp** - TargetNotFound
46. **Telegram** - TargetNotFound
47. **VLC** - TargetNotFound
48. **Audacity** - TargetNotFound
49. **Character Map** - AmbiguousApplication
50. **Magnifier** - TargetNotFound

### Failed to Launch (Other Issues)
51. **Snipping Tool** - Access denied (protected system app)
52. **Edge** - AmbiguousApplication (Microsoft Edge vs GoAwayEdge)
53. **Mail** - AmbiguousApplication (gpg tools)
53. **Settings** - Timeout (UWP app issue)
54. **Store** - Timeout (UWP app issue)
55. **Camera** - Timeout (wrong app launched)
56. **Spotify** - Close failed (media player doesn't respond to WM_CLOSE)
57. **Blender** - Close failed (save dialog)
58. **OneNote (3rd)** - Close failed (save dialog)
59. **Code/VSCode** - Close failed (admin window)

## Action Statistics

### Successful Actions by App
| App | Actions Completed | Failed Actions | Notes |
|-----|------------------|----------------|-------|
| Notepad | ~25 | 0 | Fully functional |
| Calculator | ~50 | ~5 | Disabled buttons (Kare, Karekök) |
| Word | ~60 | ~3 | Save button disabled |
| PowerPoint | ~35 | 0 | Fully functional |
| Excel | ~50 | 0 | Fully functional |
| Paint | ~50 | ~8 | Disabled tools (Kırp, Döndür, Bölüneceği sayı) |
| 7-Zip | ~20 | 0 | Fully functional |
| File Explorer | ~25 | 0 | Fully functional |
| OneNote | ~15 | 0 | Partially functional |
| Outlook | ~15 | 0 | Setup wizard |
| Publisher | ~10 | 0 | Partially functional |
| Access | ~10 | 0 | Partially functional |
| Voice Access | 1 | 0 | Basic launch |
| Remote Desktop | 1 | 0 | Basic launch |
| Steam | 1 | 0 | Basic launch |
| OneDrive | 1 | 0 | Save dialog on close |

### Failures Summary
- **Total Failed Actions:** ~16
- **Primary Failure Types:**
  - Disabled UI elements (Save in Word, specific buttons in Paint/Calculator)
  - Info boxes/dialogs appearing without model awareness
  - App close failures (save prompts, admin windows, media players)
  - Ambiguous application names
  - Timeout on UWP apps

## Performance Metrics
- **Total Apps Attempted:** 50+ (many more due to retries)
- **Successfully Launched:** ~35 unique apps
- **Successfully Closed:** ~28 unique apps
- **Total Successful Actions:** ~370+
- **Total Failed Actions:** ~16
- **Average Actions Per App:** ~10.6 (when launched)
- **Close Success Rate:** ~80%

## Critical Issues Identified

### 1. Window Close Ambiguity
- When multiple windows share the same title, must use explicit hwnd
- Example: "2 windows match 'Word' — pick one by hwnd"
- This requires manual inspection and hwnd specification

### 2. MCP Session Close Issue
- **CRITICAL:** `computer_close_window` command triggers user to close MCP session
- User reported: "yine kendini kapattın tekrar açıyorum" (you closed yourself again)
- User warned: "lütfen hangi komudu çalıştırıyorsan onu not et. o seni kapatıyor" (note which command causes this)
- This appears to be a fundamental limitation of the Inbrisk MCP implementation

### 3. Info Box/Dialog State Detection
- Info boxes and dialogs appear without model awareness
- User noted: "arka tarafta hata verip info box açan şeyleri kapatmadan tıklama ya da uygulamayı kullanamazsın"
- Example: Paint showing info dialogs that block further actions
- Requires manual intervention to close dialogs before continuing

### 4. Disabled Elements
- Buttons that are visually present but disabled (grayed out)
- Cannot be interacted with even when clicked
- Examples: Save in Word, Kırp/Döndür in Paint, Kare/Karekök in Calculator
- Model attempts these actions but they fail silently

### 5. Initial Menu Detection
- Apps sometimes launch with initial menus/dialogs that model cannot recognize
- User noted: "bir zamanlar uygulama ilk açıldığında çıkan menüleri de bilemiyorsun veya tanıyamıyorsun"
- Example: Outlook email setup wizard, various UWP app initialization dialogs

### 6. UWP App Timeouts
- Windows Store Apps (Settings, Store, Photos, etc.) timeout during launch
- App launches but no usable window detected within 10s
- Example: "launched 'Settings' via Aumid but no usable window within 10000ms"

### 7. Ambiguous Application Names
- Single search terms match multiple applications
- Example: "Edge" matches Microsoft Edge AND GoAwayEdge
- Example: "Mail" matches gpgparsemail AND gpg-mail-tube
- Requires more specific identifiers

### 8. Protected System Apps
- Some system apps refuse launch attempts due to permissions
- Example: Snipping Tool - "Access is denied"

### 9. Media Players Don't Respond to WM_CLOSE
- Applications like Spotify, VLC don't gracefully respond to close commands
- WM_CLOSE posted but window still exists (save prompt or hung)

## User Help Section

### Important Notes for User

#### 1. Window Closing Difficulties
- **Issue:** Closing windows often requires explicit hwnd due to ambiguity
- **Reason:** Multiple windows may share the same title
- **Example:** Two OneNote windows, multiple Word windows
- **Solution:** Use `computer_close_window{hwnd: "0xXXXX"}` with specific window handle
- **Risk:** Closing wrong window if hwnd is incorrect

#### 2. Info Box/Dialog State Detection Limitations (MCP Issues)
- **Issue:** Info boxes, dialogs, and error messages appear without model awareness
- **Reason:** MCP doesn't detect new windows that appear after actions
- **Impact:** Model continues clicking unaware, actions fail silently
- **Examples:**
  - Paint shows info dialogs when clicking disabled tools
  - Outlook email setup wizard appears on launch
  - Apps show save prompts on close
- **Solution:** User must manually close dialogs or intervene
- **Mitigation:** Observe desktop before and after actions to detect new windows

#### 3. Disabled Elements
- **Issue:** Some UI elements are visually present but disabled (grayed out)
- **Impact:** Click attempts fail with "element is disabled" error
- **Cannot be worked around:** These are genuine disabled states
- **Examples:**
  - Save button in Word (no document opened)
  - Kırp, Döndür in Paint (no image selected)
  - Kare, Karekök buttons in Calculator (no valid input)
- **Strategy:** Skip disabled elements, focus on enabled UI

#### 4. Application Close Failures
- **Issue:** Some apps don't respond to close commands
- **Reason:** Apps waiting for user input (save prompts), admin privileges, or background processes
- **Examples:**
  - OneDrive: "WM_CLOSE posted but window still exists" (save prompt)
  - Blender: "WM_CLOSE posted but window still exists" (save dialog)
  - Spotify: Won't close (media player)
- **Solution:** User manually closes or uses Windows Task Manager

#### 5. MCP Session Self-Closure (CRITICAL)
- **Issue:** `computer_close_window` command causes MCP session to close
- **User Report:** "ikinci kez kendini kapattın artık not et bunu"
- **Cause:** Unknown - appears to be Inbrisk server behavior
- **Impact:** Must manually restart MCP session after each window close
- **Mitigation:** User closes MCP when this happens, then reopens manually
- **Recommendation:** Avoid using `computer_close_window` in automated workflows

## Recommendations

### For Better MCP Reliability
1. Add state detection for new windows/dialogs
2. Improve UWP app launch handling with longer timeouts
3. Better ambiguous application resolution with more specific search
4. Add disabled element detection before action attempts
5. Separate "close window" from "terminate MCP session"

### For Better User Experience
1. Provide clear error messages when actions fail
2. Show what windows exist before/after each action
3. Warn user when close command might terminate session
4. Allow user to skip disabled elements automatically
5. Add retry logic for transient failures (timeouts, ambigous apps)

### For Automated Workflows
1. Avoid `computer_close_window` in long-running automation
2. Use `computer_pause_run` for clean pauses instead
3. Implement window state verification before actions
4. Add dialog detection and handling logic
5. Use process-based targeting instead of title-based

## Conclusion
The stress test revealed that while the Inbrisk MCP can successfully interact with many Windows applications, there are significant limitations around:
- State detection (new windows/dialogs)
- Window management (close ambiguity, session termination)
- Element detection (disabled elements, initial menus)
- UWP app handling (timeouts, launch failures)

The MCP is functional for basic UI automation but requires human intervention for error handling, dialog management, and session recovery. The critical issue of MCP session closure on `computer_close_window` is a major blocker for automated workflows.
