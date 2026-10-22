# Desktop redesign visual QA

final result: passed

Scope: visual fidelity of the native Avalonia shell and inspected page states; offline navigation and activity-state regression. This is not a blanket declaration that every hardware, driver, network, or long-duration case passed.

## Source visual truth

- `C:/Users/user/AppData/Local/Temp/codex-clipboard-7d402df4-f9e4-481e-a1a0-aaec5ce339bf.png` — reference overview.
- `C:/Users/user/AppData/Local/Temp/codex-clipboard-2fa59af7-35fa-4b9e-8b34-d684ab059e42.png` — reference connection page.
- `C:/Users/user/AppData/Local/Temp/codex-clipboard-1eb7bc3a-b5e3-402c-93fb-445b19d34e67.png` — reference About page.
- `C:/Users/user/AppData/Local/Temp/codex-clipboard-cb2b115d-f7b4-4bc1-bd09-1d3fbd956cad.png` — reference Settings page.
- `C:/Users/user/AppData/Local/Temp/codex-clipboard-87b88fb1-1210-4559-8418-d5ee3d88ea42.png` — Windows USB audio format/Host volume functional context, not a second shell design.
- Grounding source: `D:/GithubClone/ControllerForwardingTool/ControllerForwardingTool/Views/MainWindow.axaml`, `Views/OverviewPage.axaml`, `Views/SettingsPage.axaml`, `Views/AboutPage.axaml`, and `App.axaml`.

MIT code reuse is recorded in `THIRD_PARTY_UI_NOTICES.md`; the complete reference-project license is in `App/Notices/ControllerForwardingTool-MIT.txt` and copied to output.

## Capture method and normalization

`artifacts/UiPreview` launches the real Avalonia App in a separate preview process. It does not enter the product single-instance activation path, create a tray icon, connect to the board, or play audio. The final pass renders off-screen with `ShowActivated=false` and does not change saved user settings.

The real Windows render scale is 1.5. Implementation captures use RenderTargetBitmap at 96 DPI: 1692×889 DIP/pixels for the large client area, 1050×680 for minimum-window checks. Reference full-window screenshots are approximately 2537–2546 pixels wide at the same 1.5 desktop scale; the comparison helper removes 32 source pixels of native title-bar chrome, scales to the implementation width, and preserves aspect ratio. Small residual height differences from the separate reference window captures are not treated as content-layout defects.

Full-view combined comparison inputs, opened and inspected:

- `artifacts/ui-redesign/comparison-overview.png`
- `artifacts/ui-redesign/comparison-settings.png`
- `artifacts/ui-redesign/comparison-about.png`

Each comparison places the normalized source and implementation in the same image. Individual full-resolution captures were also inspected for typography, control clipping, and the collapsed navigation.

## Findings and fixes

- Resolved P1: the previous large TabControl navigation did not resemble the source. Replaced with the reference 216/72 DIP sidebar, 64 DIP top bar, fixed sidebar actions and a scrolling content workspace.
- Resolved P2: the initial About card was too compact. The second pass uses reference 36/40 padding, a 168 DIP icon column with 40 DIP gap, 160 DIP product icon area, 16 DIP details and 22 DIP section spacing. The post-fix comparison is `comparison-about.png`.
- Resolved P1: cumulative received packets incorrectly implied ongoing playback after USB alt 0 or driver/source loss. Active presentation now also requires USB streaming and format_selected, receiver RUNNING/not recovering/not ended, no fault, and a valid packet age ≤250 ms. Offline regression verifies alt 0 is inactive despite Accepted=1234, stale packets are inactive, and a fresh selected/streaming source is active. The two idle-source captures deliberately identify themselves as an offline presentation preview.
- Resolved shell behavior (V0.11.10): the frontend is a Windows GUI executable, so normal and tray startup do not create a console. Only the CLI branch attaches to an existing parent console; redirected output remains available. Avalonia initialization no longer calls FreeConsole.
- Resolved interaction state: USB configuration controls are disabled while an operation is in progress; a disconnect clears Host endpoint identity/availability so another remembered board cannot inherit it.
- Resolved connection recovery: previous owner intent is tracked independently from the current connection's health/CanManage flag, allowing a lost management connection to retry without letting observer sessions seize playback.
- Resolved Host volume intent: debounced writes submit the current desired volume and mute together, so clicking mute immediately after dragging the slider does not discard the volume change.

No remaining observed P0/P1/P2 visual mismatch in the compared offline states.

## Required fidelity surfaces

- Fonts/typography: Microsoft YaHei UI / Segoe UI, 24 DIP page titles, 16 DIP section headings, 12 DIP captions with 19 DIP line height, source nav and top-bar treatment retained. About details were corrected in the second pass.
- Spacing/layout: source sidebar widths, header height, body margin 20, header horizontal margin 24, button height 36, nav rows 44, card radius 10, and overview step-number/row structure are retained. The minimum window remains navigable through vertical scrolling without horizontal clipping.
- Colors/tokens: Canvas #F4F7FB, white Surface, Border #DFE6EF, ShellBorder #E4E9F0, text #172033/#667085, accent #0958D9 and selected #E8F1FF come directly from reference resources. Hero gradient and success/warning treatments use the source palette.
- Image/icon quality: source navigation chevron paths are reused, common icons use the same Windows Segoe Fluent/MDL2 icon font treatment. The product-specific About icon is the standard volume icon rather than a misleading controller brand mark. No reference screenshots are used as fake interactive UI.
- Copy/content: controller-specific steps and settings are intentionally replaced by real audio-bridge operations. Host volume, advanced local monitor gain, declared USB formats, and actual Host-selected format are distinct. alt 0 and remote-Host read-only limitations are explicit.

## Interaction evidence

`artifacts/ui-redesign/render-result.json` records:

- 14 native rendered captures.
- Nine actual bound navigation commands invoked successfully.
- Collapse command invoked successfully.
- Three activity-state regression assertions passed.
- Chinese connection WAV confirmed embedded.

The root agent separately reported actual native GUI checks: homepage one-click authentication automatically started listening through the selected Realtek output; setting USB Host volume to 35% read back as approximately 34.9999994% through an independent Windows endpoint query. These are real-session process observations, not claims derived from offline render images. Final GUI recheck after the small activity/console fixes remains owned by the main acceptance flow.

## Capture inventory

All paths below are under `SoundConvertClient/artifacts/ui-redesign/`:

1. `00-概览.png`
2. `01-开发板连接.png`
3. `02-声音.png`
4. `03-USB 声卡.png`
5. `04-网络.png`
6. `05-日志.png`
7. `06-设置.png`
8. `07-关于.png`
9. `08-更新日志.png`
10. `09-small-overview.png`
11. `10-collapsed.png`
12. `11-small-audio.png`
13. `12-idle-after-audio-overview.png`
14. `13-idle-after-audio-page.png`

## Build and remaining scope

The UI app builds with zero warnings/errors in `artifacts/redesign-final-staging`. The final package must be built from current sources so it includes the audio agent's latest recovery and format changes; this UI task does not overwrite the live standard publish folder.

P3/follow-up: full 100/125/150/200% multi-monitor interaction, very long custom names, and screen-reader walkthrough are not comprehensively tested. Driver negotiation and actual 24/96 transport remain separate hardware acceptance items. Larger buffers do not substitute for the background-thread fixes or proof of active streaming.

## Final delivered process recheck

The root flow published the current UI and final audio core together as a self-contained Windows x64 application in `artifacts/publish/win-x64` and launched that exact executable. The saved-board homepage command authenticated successfully and automatically established monitoring. With the USB source at alt 0, the actual native accessibility state reads “监听已建立，等待 USB 主机播放”; it no longer claims active playback. No separate console window was present. Firmware/driver restoration and real 96k acceptance evidence are recorded in `../docs/09-实施与实机验收记录.md`.

## V0.0.2 button follow-up

Connection now immediately precedes manual search. USB simulation and local monitoring each use one toggle with the connection button's green start/red stop styles and 130 minimum width. Pending actions disable repeat input. Monitoring remains stoppable while waiting for USB audio or source recovery; live playback metrics remain separate.

Native Avalonia + TCP fixture checks: 76 passed, with 15 captures under `../artifacts/acceptance/v0.0.2/ui-fixture`. Reviewed connection placement, simulation start/stop colors, waiting-listener stop, and stopped-listener start. The same run covers startup rejection, output-start failure cleanup, transient/fault USB states, disconnected and observer controls, and the existing small-window/collapsed-navigation layouts. This batch did not flash the board or verify physical playback.
