#!/usr/bin/env node
/**
 * Apply Nocturne's episode-nav patches to modernz.lua.
 *
 * download-modernz.js fetches an upstream copy of modernz.lua and overwrites
 * any local edits. This script re-applies our surgical patches afterwards,
 * idempotently (the `-- NOCTURNE:` marker tells us when patches are already
 * present).
 *
 * Patch summary (kept in lockstep with the markers in modernz.lua):
 *   1. Add `nocturne_has_next` / `nocturne_has_prev` to the state table.
 *   2-4. Allow playlist_prev/next OSC buttons to be visible when
 *        nocturne_has_prev/next is true (three layout sections).
 *   5. Force `ne.enabled` true and override mbtn_left_up to send
 *      script-message nocturne-prev / nocturne-next when nocturne nav is
 *      active.
 *   6. Register `nocturne-episode-nav` script-message handler that updates
 *      state.nocturne_has_prev/next and calls request_init().
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const TARGETS = [
  path.join(ROOT, 'build', 'mpv', 'portable_config', 'scripts', 'modernz.lua'),
  path.join(ROOT, 'resources', 'mpv', 'portable_config', 'scripts', 'modernz.lua'),
];

const PATCHES = [
  // 1. state table
  {
    name: 'state-table',
    find: '    playlist_count = 0,\n    playlist_pos = 0,\n    enabled = true,',
    replace: `    playlist_count = 0,
    playlist_pos = 0,
    -- NOCTURNE: episode nav context pushed by Electron main process. When
    -- nocturne_has_next/prev is true, the playlist_prev/next OSC buttons act
    -- as episode prev/next instead of mpv playlist nav.
    nocturne_has_next = false,
    nocturne_has_prev = false,
    -- NOCTURNE: end
    enabled = true,`,
  },
  // 2. bottombar layout — playlist_prev visibility
  {
    name: 'bottombar-prev',
    find: `    if user_opts.track_nextprev_buttons then
        elements["playlist_prev"].visible = (state.playlist_count > 1 or contains(user_opts.buttons_always_active, "playlist_prev")) and (osc_param.playresx >= 500 - outeroffset)
        lo = add_layout("playlist_prev")
        lo.geometry = {x = refX - (60 + (chapter_skip_buttons and 60 or 0)) - offset, y = refY - (user_opts.osc_height / 2), an = 5, w = 30, h = 24}
        lo.style = osc_styles.control_2
    end`,
    replace: `    if user_opts.track_nextprev_buttons then
        -- NOCTURNE: also visible when episode-nav context is active
        elements["playlist_prev"].visible = (state.playlist_count > 1 or state.nocturne_has_prev or contains(user_opts.buttons_always_active, "playlist_prev")) and (osc_param.playresx >= 500 - outeroffset)
        -- NOCTURNE: end
        lo = add_layout("playlist_prev")
        lo.geometry = {x = refX - (60 + (chapter_skip_buttons and 60 or 0)) - offset, y = refY - (user_opts.osc_height / 2), an = 5, w = 30, h = 24}
        lo.style = osc_styles.control_2
    end`,
  },
  // 3. bottombar layout — playlist_next visibility
  {
    name: 'bottombar-next',
    find: `    if user_opts.track_nextprev_buttons then
        elements["playlist_next"].visible = (state.playlist_count > 1 or contains(user_opts.buttons_always_active, "playlist_next")) and (osc_param.playresx >= 500 - outeroffset)
        lo = add_layout("playlist_next")
        lo.geometry = {x = refX + (60 + (chapter_skip_buttons and 60 or 0)) + offset, y = refY - (user_opts.osc_height / 2), an = 5, w = 30, h = 24}
        lo.style = osc_styles.control_2
    end`,
    replace: `    if user_opts.track_nextprev_buttons then
        -- NOCTURNE: also visible when episode-nav context is active
        elements["playlist_next"].visible = (state.playlist_count > 1 or state.nocturne_has_next or contains(user_opts.buttons_always_active, "playlist_next")) and (osc_param.playresx >= 500 - outeroffset)
        -- NOCTURNE: end
        lo = add_layout("playlist_next")
        lo.geometry = {x = refX + (60 + (chapter_skip_buttons and 60 or 0)) + offset, y = refY - (user_opts.osc_height / 2), an = 5, w = 30, h = 24}
        lo.style = osc_styles.control_2
    end`,
  },
  // 4. topbar layout
  {
    name: 'topbar',
    find: `    if user_opts.track_nextprev_buttons then
        local prev_vis = pl_pos > 1 and osc_param.playresx >= 300
        elements["playlist_prev"].visible = prev_vis
        if prev_vis then
            lo = add_layout("playlist_prev")
            lo.geometry = {x = start_x, y = refY - (user_opts.osc_height / 2), an = 5, w = 24, h = 24}
            lo.style = osc_styles.control_2
            start_x = start_x + 55
        end

        local next_vis = pl_pos < pl_count and osc_param.playresx >= 400
        elements["playlist_next"].visible = next_vis
        if next_vis then
            lo = add_layout("playlist_next")
            lo.geometry = {x = start_x, y = refY - (user_opts.osc_height / 2), an = 5, w = 24, h = 24}
            lo.style = osc_styles.control_2
            start_x = start_x + 55
        end
    end`,
    replace: `    if user_opts.track_nextprev_buttons then
        -- NOCTURNE: also visible when episode-nav context is active
        local prev_vis = (pl_pos > 1 or state.nocturne_has_prev) and osc_param.playresx >= 300
        elements["playlist_prev"].visible = prev_vis
        if prev_vis then
            lo = add_layout("playlist_prev")
            lo.geometry = {x = start_x, y = refY - (user_opts.osc_height / 2), an = 5, w = 24, h = 24}
            lo.style = osc_styles.control_2
            start_x = start_x + 55
        end

        local next_vis = (pl_pos < pl_count or state.nocturne_has_next) and osc_param.playresx >= 400
        elements["playlist_next"].visible = next_vis
        if next_vis then
            lo = add_layout("playlist_next")
            lo.geometry = {x = start_x, y = refY - (user_opts.osc_height / 2), an = 5, w = 24, h = 24}
            lo.style = osc_styles.control_2
            start_x = start_x + 55
        end
        -- NOCTURNE: end
    end`,
  },
  // 5. box-style track_nextprev_buttons gate
  {
    name: 'box-track-gate',
    find: `    local track_nextprev_buttons = user_opts.track_nextprev_buttons and state.playlist_count > 1`,
    replace: `    -- NOCTURNE: also keep buttons when episode-nav context is active
    local track_nextprev_buttons = user_opts.track_nextprev_buttons and (state.playlist_count > 1 or state.nocturne_has_prev or state.nocturne_has_next)
    -- NOCTURNE: end`,
  },
  // 6. button registration: extend ne.enabled + override eventresponder
  {
    name: 'button-registration',
    find: `    -- playlist buttons
    -- prev
    ne = new_element("playlist_prev", "button")
    ne.content = icons.previous
    ne.enabled = (pl_pos > 1) or (loop ~= "no") or contains(user_opts.buttons_always_active, "playlist_prev")
    bind_buttons("playlist_prev")

    --next
    ne = new_element("playlist_next", "button")
    ne.content = icons.next
    ne.enabled = (have_pl and (pl_pos < pl_count)) or (loop ~= "no") or contains(user_opts.buttons_always_active, "playlist_next")
    bind_buttons("playlist_next")`,
    replace: `    -- playlist buttons
    -- prev
    ne = new_element("playlist_prev", "button")
    ne.content = icons.previous
    -- NOCTURNE: also enabled when episode-nav context is active
    ne.enabled = (pl_pos > 1) or (loop ~= "no") or contains(user_opts.buttons_always_active, "playlist_prev") or state.nocturne_has_prev
    bind_buttons("playlist_prev")
    -- NOCTURNE: when nocturne episode-nav is active, route left-click to the
    -- main process via script-message instead of mpv's \`playlist-prev\`.
    if state.nocturne_has_prev then
        elements["playlist_prev"].eventresponder["mbtn_left_up"] = function()
            mp.commandv("script-message", "nocturne-prev")
        end
    end
    -- NOCTURNE: end

    --next
    ne = new_element("playlist_next", "button")
    ne.content = icons.next
    -- NOCTURNE: also enabled when episode-nav context is active
    ne.enabled = (have_pl and (pl_pos < pl_count)) or (loop ~= "no") or contains(user_opts.buttons_always_active, "playlist_next") or state.nocturne_has_next
    bind_buttons("playlist_next")
    -- NOCTURNE: when nocturne episode-nav is active, route left-click to the
    -- main process via script-message instead of mpv's \`playlist-next\`.
    if state.nocturne_has_next then
        elements["playlist_next"].eventresponder["mbtn_left_up"] = function()
            mp.commandv("script-message", "nocturne-next")
        end
    end
    -- NOCTURNE: end`,
  },
  // 7. hide the playlist button (Nocturne never uses mpv playlists)
  {
    name: 'hide-playlist-button',
    find: `    ne = new_element("playlist", "button")
    ne.enabled = have_pl or not user_opts.hide_empty_playlist_button
    ne.off = not have_pl and user_opts.gray_empty_playlist_button
    ne.content = icons.playlist
    ne.tooltipF = user_opts.tooltip_hints and (have_pl and locale.playlist .. " [" .. pl_pos .. "/" .. pl_count .. "]" or locale.playlist .. " / " .. locale.menu) or nil
    ne.nothingavailable = locale.no_playlist
    bind_buttons("playlist")`,
    replace: `    ne = new_element("playlist", "button")
    ne.enabled = have_pl or not user_opts.hide_empty_playlist_button
    ne.off = not have_pl and user_opts.gray_empty_playlist_button
    ne.content = icons.playlist
    ne.tooltipF = user_opts.tooltip_hints and (have_pl and locale.playlist .. " [" .. pl_pos .. "/" .. pl_count .. "]" or locale.playlist .. " / " .. locale.menu) or nil
    ne.nothingavailable = locale.no_playlist
    bind_buttons("playlist")
    -- NOCTURNE: start — hide the playlist button. Nocturne uses loadfile-replace
    -- so there is never an mpv-side playlist; the prev/next OSC buttons are
    -- repurposed for episode nav. Code preserved in case real playlist support
    -- is added later.
    elements["playlist"].visible = false
    elements["playlist"].enabled = false
    -- NOCTURNE: end`,
  },
  // 8. script-message handler registration
  {
    name: 'script-message-handler',
    find: `mp.register_script_message("thumbfast-info", function(json)
    local data = utils.parse_json(json)
    if type(data) ~= "table" or not data.width or not data.height then
        msg.error("thumbfast-info: received json didn't produce a table with thumbnail information")
    else
        thumbfast = data
    end
end)`,
    replace: `mp.register_script_message("thumbfast-info", function(json)
    local data = utils.parse_json(json)
    if type(data) ~= "table" or not data.width or not data.height then
        msg.error("thumbfast-info: received json didn't produce a table with thumbnail information")
    else
        thumbfast = data
    end
end)

-- NOCTURNE: receive episode-nav state from the Electron main process. The
-- main process pushes "true"/"false" strings (not booleans) because mpv
-- script-message args are always strings. request_init() forces osc_init()
-- to re-run so the playlist_prev/next buttons get fresh visibility +
-- eventresponder bindings.
mp.register_script_message("nocturne-episode-nav", function(has_prev, has_next)
    state.nocturne_has_prev = (has_prev == "true")
    state.nocturne_has_next = (has_next == "true")
    request_init()
end)
-- NOCTURNE: end`,
  },
];

function patchFile(file) {
  if (!fs.existsSync(file)) {
    console.warn(`  miss: ${path.relative(ROOT, file)} (run npm run download-modernz first)`);
    return { ok: false };
  }
  let src = fs.readFileSync(file, 'utf8');
  let applied = 0;
  let skipped = 0;
  for (const p of PATCHES) {
    // Per-patch idempotency: if the replace text is already present, this
    // patch has been applied previously — skip silently. Lets us add new
    // patches without forcing a fresh download.
    if (src.includes(p.replace)) {
      skipped++;
      continue;
    }
    if (!src.includes(p.find)) {
      console.error(`  FAIL: ${path.relative(ROOT, file)} — patch "${p.name}" anchor missing and replace not present`);
      console.error('         (upstream modernz.lua may have changed; update scripts/patch-modernz.js)');
      return { ok: false };
    }
    src = src.replace(p.find, p.replace);
    applied++;
  }
  if (applied > 0) {
    fs.writeFileSync(file, src);
    console.log(`  patched: ${path.relative(ROOT, file)} (${applied} new, ${skipped} already applied)`);
  } else {
    console.log(`  skip: ${path.relative(ROOT, file)} (all ${skipped} patches already applied)`);
  }
  return { ok: true, applied };
}

let allOk = true;
for (const f of TARGETS) {
  const r = patchFile(f);
  if (!r.ok) allOk = false;
}
if (!allOk) process.exit(1);
console.log('\nmodernz.lua patched with Nocturne episode-nav extensions.');
