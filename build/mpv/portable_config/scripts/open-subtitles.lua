-- open-subtitles.lua — Download subtitles from OpenSubtitles.com
-- Works alongside any OSC (ModernZ, uosc, etc.)
-- Press 'b' to search and download subtitles

local msg = require('mp.msg')
local utils = require('mp.utils')

local API_URL = "https://api.opensubtitles.com/api/v1"
local USER_AGENT = "Nocturne v2.0.0"

-- Languages to search for (ISO 639-1)
local LANGUAGES = "en"  -- comma-separated: "en,hi,es"

local function get_media_info()
  local title = mp.get_property("media-title") or mp.get_property("filename/no-ext") or ""
  local path = mp.get_property("path") or ""
  local duration = mp.get_property_number("duration") or 0
  return title, path, duration
end

local function show_message(text, duration)
  mp.osd_message(text, duration or 3)
end

local function http_get(url, headers)
  -- Use PowerShell to make HTTP requests (Windows)
  -- This avoids needing curl or wget
  local header_args = ""
  if headers then
    for k, v in pairs(headers) do
      header_args = header_args .. string.format(" -Headers @{'%s'='%s'}", k, v)
    end
  end

  local ps_cmd = string.format(
    'powershell -NoProfile -Command "try { $r = Invoke-RestMethod -Uri \'%s\' -Method Get%s -TimeoutSec 10; $r | ConvertTo-Json -Depth 10 } catch { Write-Error $_.Exception.Message }"',
    url, header_args
  )

  local result = utils.subprocess({
    args = {"cmd", "/c", ps_cmd},
    capture_stdout = true,
    capture_stderr = true,
  })

  if result.status ~= 0 then
    msg.error("HTTP request failed: " .. (result.stderr or "unknown error"))
    return nil
  end

  return result.stdout
end

local function http_get_curl(url, headers)
  -- Alternative: use curl if available
  local args = {"curl", "-s", "-L", "--max-time", "10", url}
  if headers then
    for k, v in pairs(headers) do
      table.insert(args, "-H")
      table.insert(args, k .. ": " .. v)
    end
  end

  local result = utils.subprocess({ args = args, capture_stdout = true, capture_stderr = true })
  if result.status ~= 0 then return nil end
  return result.stdout
end

local function search_subtitles(query)
  show_message("Searching subtitles for: " .. query, 5)

  local search_url = string.format(
    "%s/subtitles?query=%s&languages=%s&order_by=download_count&order_direction=desc",
    API_URL,
    query:gsub(" ", "+"),
    LANGUAGES
  )

  local headers = {
    ["Api-Key"] = "iesb5LW2mPbnxMn2mF2xWC59HKPYOWOA",  -- Public OpenSubtitles API key
    ["User-Agent"] = USER_AGENT,
    ["Content-Type"] = "application/json",
  }

  -- Try curl first, fall back to PowerShell
  local response = http_get_curl(search_url, headers)
  if not response then
    response = http_get(search_url, headers)
  end

  if not response then
    show_message("Failed to search subtitles", 3)
    return nil
  end

  local data = utils.parse_json(response)
  if not data or not data.data then
    show_message("No subtitles found", 3)
    return nil
  end

  return data.data
end

local function download_subtitle(file_id, filename)
  show_message("Downloading subtitle...", 5)

  local download_url = API_URL .. "/download"
  local body = string.format('{"file_id": %d}', file_id)

  local headers = {
    ["Api-Key"] = "iesb5LW2mPbnxMn2mF2xWC59HKPYOWOA",
    ["User-Agent"] = USER_AGENT,
    ["Content-Type"] = "application/json",
  }

  -- Use curl for POST
  local result = utils.subprocess({
    args = {
      "curl", "-s", "-L", "--max-time", "15",
      "-X", "POST",
      "-H", "Api-Key: " .. headers["Api-Key"],
      "-H", "User-Agent: " .. headers["User-Agent"],
      "-H", "Content-Type: application/json",
      "-d", body,
      download_url
    },
    capture_stdout = true,
  })

  if result.status ~= 0 or not result.stdout then
    show_message("Download failed", 3)
    return nil
  end

  local data = utils.parse_json(result.stdout)
  if not data or not data.link then
    show_message("Download failed — rate limit may be reached", 3)
    return nil
  end

  -- Download the actual subtitle file
  local media_path = mp.get_property("path") or ""

  -- Save next to the video file if local, otherwise to temp
  local save_path
  if media_path:match("^http") then
    save_path = os.getenv("TEMP") .. "\\" .. filename
  else
    local dir = media_path:match("(.+)[\\/]") or "."
    save_path = dir .. "\\" .. filename
  end

  local dl_result = utils.subprocess({
    args = {"curl", "-s", "-L", "--max-time", "15", "-o", save_path, data.link},
    capture_stdout = true,
  })

  if dl_result.status ~= 0 then
    show_message("Failed to save subtitle file", 3)
    return nil
  end

  return save_path
end

local function select_and_download()
  local title = get_media_info()
  if not title or title == "" then
    show_message("No media playing", 3)
    return
  end

  -- Clean the title (remove year, quality tags, etc.)
  local clean_title = title
    :gsub("%([^)]*%)", "")  -- remove (2024), (4K), etc.
    :gsub("%[[^%]]*%]", "") -- remove [1080p], etc.
    :gsub("%d%d%d%d%d+p?", "") -- remove 1080p, 2160, etc.
    :gsub("[%.%_]", " ")   -- replace dots/underscores with spaces
    :gsub("%s+", " ")      -- collapse multiple spaces
    :match("^%s*(.-)%s*$") -- trim

  local results = search_subtitles(clean_title)
  if not results or #results == 0 then
    show_message("No subtitles found for: " .. clean_title, 5)
    return
  end

  -- Show top 5 results via OSD and let user pick with number keys
  local menu_text = "Subtitles found — press 1-5 to download:\n\n"
  local max_results = math.min(5, #results)

  local items = {}
  for i = 1, max_results do
    local sub = results[i]
    local attrs = sub.attributes or {}
    local lang = attrs.language or "?"
    local release = attrs.release or attrs.feature_details and attrs.feature_details.title or "Unknown"
    local fps = attrs.fps or ""
    local hearing = attrs.hearing_impaired and " [HI]" or ""
    local dl_count = attrs.download_count or 0

    menu_text = menu_text .. string.format(
      "%d. [%s] %s%s (dl:%d)\n",
      i, lang:upper(), release, hearing, dl_count
    )

    -- Get the file info
    if sub.attributes and sub.attributes.files and #sub.attributes.files > 0 then
      items[i] = {
        file_id = sub.attributes.files[1].file_id,
        filename = sub.attributes.files[1].file_name or ("subtitle_" .. i .. ".srt"),
      }
    end
  end

  show_message(menu_text, 15)

  -- Bind number keys temporarily
  for i = 1, max_results do
    local key = tostring(i)
    mp.add_forced_key_binding(key, "opensub-pick-" .. key, function()
      -- Remove all bindings
      for j = 1, 5 do
        mp.remove_key_binding("opensub-pick-" .. tostring(j))
      end
      mp.remove_key_binding("opensub-cancel")

      if items[i] then
        local path = download_subtitle(items[i].file_id, items[i].filename)
        if path then
          mp.commandv("sub-add", path, "auto")
          show_message("Subtitle loaded: " .. items[i].filename, 5)
        end
      end
    end)
  end

  -- ESC to cancel
  mp.add_forced_key_binding("ESC", "opensub-cancel", function()
    for j = 1, 5 do
      mp.remove_key_binding("opensub-pick-" .. tostring(j))
    end
    mp.remove_key_binding("opensub-cancel")
    show_message("Subtitle search cancelled", 2)
  end)
end

-- Bind 'b' to trigger subtitle search
mp.add_forced_key_binding("b", "opensub-search", select_and_download)

msg.info("OpenSubtitles script loaded — press 'b' to search subtitles")
