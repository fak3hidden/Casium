--[[
    Casium loader — starter module
    ==============================
    Returned by https://casium.top/loader.lua (game:HttpGet). It validates a
    licence key against the keys API on casium.top and hands back a small API
    surface. Extend it freely; the only part the site depends on is the call
    to POST /api/validate, which is what the WPF client does too.

        local Casium = loadstring(game:HttpGet("https://casium.top/loader.lua"))()
        local session = Casium:Auth("casium-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx")
        if not session then return end

    The key itself is whatever the console issued — the client passes it in.
]]

local HTTP_ENDPOINT = "https://casium.top/api/validate"

local HttpService = game:GetService("HttpService")

---------------------------------------------------------------- request layer

-- Executors expose `request`; fall back to HttpService when they don't.
local function post(url, payload)
    local body = HttpService:JSONEncode(payload)

    if request then
        local response = request({
            Url = url,
            Method = "POST",
            Headers = { ["Content-Type"] = "application/json" },
            Body = body,
        })
        return response.StatusCode, response.Body
    end

    return 200, HttpService:JSONDecode(HttpService:PostAsync(url, body, Enum.ContentType.ApplicationJson))
end

-------------------------------------------------------------------- the API

local Casium = {}
Casium.__index = Casium

Casium.session = nil -- { key, expiresAt, lifetime, remainingSeconds }

--- Validate a key with casium.top. Returns the session table, or nil + reason.
function Casium:Auth(key)
    local ok, status, body = pcall(post, HTTP_ENDPOINT, { key = tostring(key) })
    if not ok then
        warn("[casium] could not reach casium.top: " .. tostring(status))
        return nil, "network"
    end

    local data = type(body) == "table" and body or pcall(HttpService.JSONDecode, HttpService, body)
    if type(data) ~= "table" then
        return nil, "bad_response"
    end
    if not data.valid then
        warn(("[casium] key rejected: %s"):format(data.reason or "unknown"))
        return nil, data.reason
    end

    self.session = {
        key = data.key,
        lifetime = data.lifetime,
        expiresAt = data.expiresAt,      -- ISO-8601, or nil for lifetime keys
        remainingSeconds = data.remainingSeconds,
        checkedAt = data.serverTime,
    }
    return self.session
end

--- True once Auth has succeeded for this session.
function Casium:IsAttached()
    return self.session ~= nil
end

--- In-client toast. Swap the implementation for your UI library.
function Casium:Notify(title, text)
    if game:GetService("RunService"):IsStudio() then
        print(("[casium] %s — %s"):format(title, tostring(text)))
        return
    end
    game:GetService("StarterGui"):SetCore("SendNotification", {
        Title = title,
        Text = tostring(text),
        Duration = 5,
    })
end

--- Execute a script from the client workspace by file name.
function Casium:Run(name)
    local source = game:HttpGet(("https://casium.top/scripts/%s"):format(name))
    return loadstring(source)()
end

--- Placeholder hub list — replace with your real catalogue.
function Casium:Hub()
    return {
        { name = "Infinite Yield", version = "5.9.1" },
        { name = "Dex Explorer", version = "4.1.0" },
    }
end

return Casium
