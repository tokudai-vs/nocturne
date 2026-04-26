-- Nocturne anchored selector menu (audio / subtitle / playlist).
-- Replaces mpv's built-in select.lua centered prompt with an ASS overlay
-- anchored bottom-right above the OSC button row, using clean track titles.

local mp = require 'mp'
local assdraw = require 'mp.assdraw'

-- ----------------------------------------------------------------------------
-- Theme. ASS color literals are BGR, alpha is 00=opaque, FF=transparent.
-- Amber accent #E5A00D in RGB == #0DA0E5 in BGR.
-- ----------------------------------------------------------------------------
local THEME = {
    bg                 = "1A1A1A",
    border             = "555555",
    accent             = "0DA0E5",
    text               = "FFFFFF",
    text_active        = "FFFFFF",
    text_dim           = "AAAAAA",
    bg_alpha           = "30",
    selected_bg_alpha  = "80",
    border_alpha       = "60",

    width              = 380,
    item_height        = 36,
    title_height       = 30,
    padding_x          = 16,
    padding_y          = 12,
    corner_radius      = 8,
    margin_right       = 24,
    margin_bottom      = 110,

    font               = "Segoe UI",
    title_size         = 16,
    item_size          = 18,
    max_visible        = 10,
}

-- ----------------------------------------------------------------------------
-- Language / codec / format mappings.
-- ----------------------------------------------------------------------------
local LANG_NAMES = {
    en = "English", eng = "English",
    hi = "Hindi", hin = "Hindi",
    es = "Spanish", spa = "Spanish",
    fr = "French", fra = "French", fre = "French",
    de = "German", deu = "German", ger = "German",
    it = "Italian", ita = "Italian",
    ja = "Japanese", jpn = "Japanese",
    ko = "Korean", kor = "Korean",
    zh = "Chinese", zho = "Chinese", chi = "Chinese",
    pt = "Portuguese", por = "Portuguese",
    ru = "Russian", rus = "Russian",
    ar = "Arabic", ara = "Arabic",
    ta = "Tamil", tam = "Tamil",
    te = "Telugu", tel = "Telugu",
    bn = "Bengali", ben = "Bengali",
    pa = "Punjabi", pan = "Punjabi",
    mr = "Marathi", mar = "Marathi",
    gu = "Gujarati", guj = "Gujarati",
    ml = "Malayalam", mal = "Malayalam",
    kn = "Kannada", kan = "Kannada",
    ur = "Urdu", urd = "Urdu",
    nl = "Dutch", nld = "Dutch", dut = "Dutch",
    pl = "Polish", pol = "Polish",
    tr = "Turkish", tur = "Turkish",
    th = "Thai", tha = "Thai",
    vi = "Vietnamese", vie = "Vietnamese",
    id = "Indonesian", ind = "Indonesian",
    ms = "Malay", msa = "Malay", may = "Malay",
    sv = "Swedish", swe = "Swedish",
    no = "Norwegian", nor = "Norwegian",
    da = "Danish", dan = "Danish",
    fi = "Finnish", fin = "Finnish",
    cs = "Czech", ces = "Czech", cze = "Czech",
    el = "Greek", ell = "Greek", gre = "Greek",
    he = "Hebrew", heb = "Hebrew",
    fa = "Persian", per = "Persian", fas = "Persian",
    uk = "Ukrainian", ukr = "Ukrainian",
    ro = "Romanian", ron = "Romanian", rum = "Romanian",
    hu = "Hungarian", hun = "Hungarian",
}

local AUDIO_CODECS = {
    ac3 = "AC3", eac3 = "E-AC3", aac = "AAC",
    dts = "DTS", ["dts-hd"] = "DTS-HD", truehd = "TrueHD",
    flac = "FLAC", mp3 = "MP3", opus = "Opus", vorbis = "Vorbis",
    pcm = "PCM", pcm_s16le = "PCM", pcm_s24le = "PCM",
}

local SUB_FORMATS = {
    subrip = "SubRip", srt = "SubRip",
    ass = "ASS", ssa = "SSA",
    pgs = "PGS", hdmv_pgs_subtitle = "PGS",
    dvd_subtitle = "DVD", dvb_subtitle = "DVB",
    webvtt = "WebVTT", vtt = "WebVTT",
    mov_text = "MP4", eia_608 = "CC",
}

-- ----------------------------------------------------------------------------
-- Menu state.
-- ----------------------------------------------------------------------------
local menu = {
    open      = false,
    kind      = nil,
    title     = "",
    items     = nil,
    selected  = 1,
    scroll    = 0,
    overlay   = nil,
    geom      = nil,
    osd_w     = 1920,
    osd_h     = 1080,
}

-- forward declarations
local redraw, close_menu, open_menu, apply_selection, handle_click
local KEY_BINDS

-- ----------------------------------------------------------------------------
-- Formatters.
-- ----------------------------------------------------------------------------
local function fmt_lang(code)
    if not code or code == "" then return nil end
    local lower = code:lower()
    return LANG_NAMES[lower] or code:upper()
end

local function fmt_codec(codec, table_)
    if not codec or codec == "" then return nil end
    return table_[codec:lower()] or codec:upper()
end

local function fmt_audio_track(track)
    local parts = {}
    local lang = fmt_lang(track.lang)
    if lang then parts[#parts + 1] = lang end
    local codec = fmt_codec(track.codec, AUDIO_CODECS)
    if codec then parts[#parts + 1] = codec end
    local channels = track["demux-channel-count"] or track.audio_channels
    if type(channels) == "number" and channels > 0 then
        parts[#parts + 1] = tostring(channels) .. "ch"
    end

    local label
    if #parts > 0 then
        label = table.concat(parts, " · ")
    elseif track.title and track.title ~= "" then
        label = track.title
    else
        label = "Track " .. tostring(track.id)
    end
    if track.default then label = label .. " (Default)" end
    if track.forced then label = label .. " (Forced)" end
    return label
end

local function fmt_sub_track(track)
    local lang = fmt_lang(track.lang)
    local label
    if lang then
        label = lang
    elseif track.title and track.title ~= "" then
        label = track.title
    else
        label = "Track " .. tostring(track.id)
    end
    local fmt = fmt_codec(track.codec, SUB_FORMATS)
    if fmt then label = label .. " (" .. fmt .. ")" end
    if track.default then label = label .. " · Default" end
    if track.forced then label = label .. " · Forced" end
    if track.external then label = label .. " · External" end
    return label
end

local function basename(path)
    if not path then return "" end
    return (path:gsub("\\", "/"):match("([^/]+)$")) or path
end

local function strip_ext(name)
    return (name:gsub("%.[^.]+$", ""))
end

local function fmt_playlist_entry(entry, idx)
    if entry.title and entry.title ~= "" then return entry.title end
    if entry.filename and entry.filename ~= "" then
        return strip_ext(basename(entry.filename))
    end
    return "Item " .. tostring(idx)
end

-- Truncate to a UTF-8 character budget so long track titles don't overflow the
-- 380px panel. Falls back to byte length if utf8 is unavailable (over-truncates
-- multi-byte but stays visually safe).
local function truncate(s, max_len)
    if not s then return "" end
    max_len = max_len or 38
    local n = utf8 and utf8.len and utf8.len(s)
    if n then
        if n > max_len then
            return s:sub(1, utf8.offset(s, max_len) - 1) .. "…"
        end
        return s
    end
    if #s > max_len then return s:sub(1, max_len - 1) .. "…" end
    return s
end

-- ASS escape: literal braces and backslashes confuse the renderer.
local function ass_escape(s)
    if not s then return "" end
    s = s:gsub("\\", "\\\\")
    s = s:gsub("{", "\\{")
    s = s:gsub("}", "\\}")
    s = s:gsub("\n", " ")
    return s
end

-- ----------------------------------------------------------------------------
-- Item builders.
-- ----------------------------------------------------------------------------
local function build_audio_items()
    local tracks = mp.get_property_native("track-list") or {}
    local items = {}
    for _, t in ipairs(tracks) do
        if t.type == "audio" then
            items[#items + 1] = {
                label  = fmt_audio_track(t),
                value  = tostring(t.id),
                active = t.selected == true,
            }
        end
    end
    return items, "Select audio track"
end

local function build_sub_items()
    local tracks = mp.get_property_native("track-list") or {}
    local current_sid = mp.get_property("sid")
    local items = {
        { label = "Off", value = "no", active = (current_sid == nil or current_sid == "no") },
    }
    for _, t in ipairs(tracks) do
        if t.type == "sub" then
            items[#items + 1] = {
                label  = fmt_sub_track(t),
                value  = tostring(t.id),
                active = t.selected == true,
            }
        end
    end
    return items, "Select subtitle"
end

local function build_playlist_items()
    local pl = mp.get_property_native("playlist") or {}
    local items = {}
    for i, entry in ipairs(pl) do
        items[#items + 1] = {
            label  = fmt_playlist_entry(entry, i),
            value  = tostring(i - 1),
            active = entry.current == true,
        }
    end
    return items, "Select playlist entry"
end

-- ----------------------------------------------------------------------------
-- ASS rendering.
-- ----------------------------------------------------------------------------
local function build_ass()
    if not menu.open or not menu.items or #menu.items == 0 then return "" end

    local total = #menu.items
    local visible = math.min(total, THEME.max_visible)

    if menu.selected < menu.scroll + 1 then
        menu.scroll = menu.selected - 1
    elseif menu.selected > menu.scroll + visible then
        menu.scroll = menu.selected - visible
    end
    menu.scroll = math.max(0, math.min(menu.scroll, total - visible))

    local body_h  = visible * THEME.item_height
    local total_h = THEME.title_height + body_h + THEME.padding_y * 2
    local total_w = THEME.width
    local x_right = menu.osd_w - THEME.margin_right
    local y_bot   = menu.osd_h - THEME.margin_bottom
    local x_left  = x_right - total_w
    local y_top   = y_bot - total_h

    local ass = assdraw.ass_new()

    -- Panel background (rounded).
    ass:new_event()
    ass:pos(0, 0)
    ass:append(string.format(
        "{\\an7\\bord1\\shad0\\1c&H%s&\\1a&H%s&\\3c&H%s&\\3a&H%s&}",
        THEME.bg, THEME.bg_alpha, THEME.border, THEME.border_alpha))
    ass:draw_start()
    ass:round_rect_cw(x_left, y_top, x_right, y_bot, THEME.corner_radius)
    ass:draw_stop()

    -- Title.
    ass:new_event()
    ass:pos(x_left + THEME.padding_x, y_top + THEME.padding_y)
    ass:append(string.format(
        "{\\an7\\bord0\\shad0\\fs%d\\fn%s\\1c&H%s&\\b1}",
        THEME.title_size, THEME.font, THEME.text_dim))
    ass:append(ass_escape(menu.title))

    -- Items.
    local list_y0 = y_top + THEME.padding_y + THEME.title_height
    for i = 1, visible do
        local idx = menu.scroll + i
        local item = menu.items[idx]
        if item then
            local row_y      = list_y0 + (i - 1) * THEME.item_height
            local is_selected = (idx == menu.selected)

            if is_selected then
                ass:new_event()
                ass:pos(0, 0)
                ass:append(string.format(
                    "{\\an7\\bord0\\shad0\\1c&H%s&\\1a&H%s&}",
                    THEME.accent, THEME.selected_bg_alpha))
                ass:draw_start()
                ass:round_rect_cw(
                    x_left + 6, row_y + 2,
                    x_right - 6, row_y + THEME.item_height - 2, 4)
                ass:draw_stop()
            end

            if item.active then
                ass:new_event()
                ass:pos(x_left + THEME.padding_x + 2, row_y + THEME.item_height / 2)
                ass:append(string.format(
                    "{\\an5\\bord0\\shad0\\1c&H%s&}", THEME.accent))
                ass:draw_start()
                ass:round_rect_cw(-4, -4, 4, 4, 4)
                ass:draw_stop()
            end

            local color = (item.active and not is_selected) and THEME.text_active
                or (is_selected and THEME.text)
                or THEME.text_dim
            local weight = (is_selected or item.active) and 1 or 0
            ass:new_event()
            ass:pos(x_left + THEME.padding_x + 18, row_y + THEME.item_height / 2)
            ass:append(string.format(
                "{\\an4\\bord0\\shad0\\fs%d\\fn%s\\1c&H%s&\\b%d}",
                THEME.item_size, THEME.font, color, weight))
            ass:append(ass_escape(truncate(item.label, 38)))
        end
    end

    -- Scroll indicator.
    if total > visible then
        local sb_x      = x_right - 5
        local sb_top    = list_y0
        local sb_bot    = list_y0 + body_h
        local frac      = visible / total
        local thumb_h   = (sb_bot - sb_top) * frac
        local denom     = math.max(1, total - visible)
        local thumb_y   = sb_top + ((sb_bot - sb_top - thumb_h) * (menu.scroll / denom))
        ass:new_event()
        ass:pos(0, 0)
        ass:append(string.format("{\\an7\\bord0\\shad0\\1c&H%s&\\1a&H80&}", THEME.text_dim))
        ass:draw_start()
        ass:rect_cw(sb_x, thumb_y, sb_x + 2, thumb_y + thumb_h)
        ass:draw_stop()
    end

    menu.geom = {
        x_left = x_left, x_right = x_right,
        y_top = y_top, y_bot = y_bot,
        list_y0 = list_y0, item_height = THEME.item_height,
        visible = visible,
    }

    return ass.text
end

-- ----------------------------------------------------------------------------
-- Render / actions.
-- ----------------------------------------------------------------------------
redraw = function()
    if not menu.open then return end
    if not menu.overlay then
        menu.overlay = mp.create_osd_overlay("ass-events")
    end
    menu.osd_w = mp.get_property_number("osd-width") or 1920
    menu.osd_h = mp.get_property_number("osd-height") or 1080
    menu.overlay.res_x = menu.osd_w
    menu.overlay.res_y = menu.osd_h
    menu.overlay.data = build_ass()
    menu.overlay:update()
end

apply_selection = function()
    if not menu.items or not menu.items[menu.selected] then return end
    local item = menu.items[menu.selected]
    if menu.kind == "audio" then
        mp.commandv("set", "aid", item.value)
    elseif menu.kind == "sub" then
        mp.commandv("set", "sid", item.value)
    elseif menu.kind == "playlist" then
        mp.commandv("playlist-play-index", item.value)
    end
end

close_menu = function()
    if not menu.open then return end
    if KEY_BINDS then
        for _, b in ipairs(KEY_BINDS) do
            mp.remove_key_binding(b[2])
        end
    end
    if menu.overlay then
        menu.overlay:remove()
        menu.overlay = nil
    end
    menu.open  = false
    menu.items = nil
    menu.kind  = nil
    menu.geom  = nil
end

handle_click = function()
    if not menu.geom then close_menu(); return end
    local mx, my = mp.get_mouse_pos()
    local g = menu.geom
    if mx < g.x_left or mx > g.x_right or my < g.y_top or my > g.y_bot then
        close_menu()
        return
    end
    if my < g.list_y0 then return end
    local row = math.floor((my - g.list_y0) / g.item_height) + 1
    if row < 1 or row > g.visible then return end
    local idx = menu.scroll + row
    if idx < 1 or idx > #menu.items then return end
    menu.selected = idx
    apply_selection()
    close_menu()
end

local function nav_up()    menu.selected = math.max(1, menu.selected - 1); redraw() end
local function nav_down()  menu.selected = math.min(#menu.items, menu.selected + 1); redraw() end
local function nav_home()  menu.selected = 1; redraw() end
local function nav_end()   menu.selected = #menu.items; redraw() end
local function nav_pgup()  menu.selected = math.max(1, menu.selected - THEME.max_visible); redraw() end
local function nav_pgdn()  menu.selected = math.min(#menu.items, menu.selected + THEME.max_visible); redraw() end
local function nav_enter() apply_selection(); close_menu() end
local function nav_esc()   close_menu() end
local function nav_click() handle_click() end

KEY_BINDS = {
    { "UP",         "ns-up",    nav_up },
    { "DOWN",       "ns-down",  nav_down },
    { "HOME",       "ns-home",  nav_home },
    { "END",        "ns-end",   nav_end },
    { "PGUP",       "ns-pgup",  nav_pgup },
    { "PGDWN",      "ns-pgdn",  nav_pgdn },
    { "ENTER",      "ns-enter", nav_enter },
    { "KP_ENTER",   "ns-kpent", nav_enter },
    { "ESC",        "ns-esc",   nav_esc },
    { "MBTN_LEFT",  "ns-click", nav_click },
    { "WHEEL_UP",   "ns-wup",   nav_up },
    { "WHEEL_DOWN", "ns-wdn",   nav_down },
}

open_menu = function(kind)
    if menu.open and menu.kind == kind then
        close_menu()
        return
    end
    if menu.open then close_menu() end

    local items, title
    if kind == "audio" then
        items, title = build_audio_items()
    elseif kind == "sub" then
        items, title = build_sub_items()
    elseif kind == "playlist" then
        items, title = build_playlist_items()
    end
    if not items or #items == 0 then return end

    menu.kind     = kind
    menu.title    = title
    menu.items    = items
    menu.scroll   = 0
    menu.selected = 1
    for i, it in ipairs(items) do
        if it.active then menu.selected = i; break end
    end
    menu.open = true

    for _, b in ipairs(KEY_BINDS) do
        mp.add_forced_key_binding(b[1], b[2], b[3], { repeatable = true })
    end
    redraw()
end

-- ----------------------------------------------------------------------------
-- Hover-to-highlight (cheap: only redraws when row index actually changes).
-- ----------------------------------------------------------------------------
mp.observe_property("mouse-pos", "native", function(_, val)
    if not menu.open or not val or not menu.geom then return end
    local mx, my = val.x, val.y
    if not mx or not my then return end
    local g = menu.geom
    if mx < g.x_left or mx > g.x_right or my < g.list_y0 or my >= g.list_y0 + g.visible * g.item_height then
        return
    end
    local row = math.floor((my - g.list_y0) / g.item_height) + 1
    local idx = menu.scroll + row
    if idx >= 1 and idx <= #menu.items and idx ~= menu.selected then
        menu.selected = idx
        redraw()
    end
end)

mp.observe_property("osd-width",  "number", function() if menu.open then redraw() end end)
mp.observe_property("osd-height", "number", function() if menu.open then redraw() end end)

mp.register_event("end-file", function() if menu.open then close_menu() end end)

-- ----------------------------------------------------------------------------
-- Public bindings — referenced by modernz.conf as
--   script-binding nocturne_select/select-{aid,sid,playlist}
-- (mpv replaces non-alphanumerics in the filename with `_` for the script id.)
-- ----------------------------------------------------------------------------
mp.add_key_binding(nil, "select-aid",      function() open_menu("audio") end)
mp.add_key_binding(nil, "select-sid",      function() open_menu("sub") end)
mp.add_key_binding(nil, "select-playlist", function() open_menu("playlist") end)
