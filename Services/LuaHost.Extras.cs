using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;

namespace Apathy.Services;

// More for scripts: require() for shared modules, storage.* (values kept between runs), events.*
// (scripts talking to each other), spawn() / wait() (code that pauses without blocking apathy),
// http.request() with headers, and small helpers (inspect, table.*, base64, hash, util).
public sealed partial class LuaHost
{
    // A spawn(fn) task: a coroutine resumed by Tick whenever its wait() is over.
    private sealed class LuaTask(Coroutine coroutine, DynValue[] args)
    {
        public readonly Coroutine Coroutine = coroutine;
        public DynValue[]? StartArgs = args; // set until the first resume
        public double Due;
        public bool Cancelled;
    }

    private const int MaxStorageBytes = 1024 * 1024;
    private const int MaxValueDepth = 32;

    // events.emit queues; Tick delivers, so a handler never runs inside the emitting script's call.
    private readonly Queue<(string Name, object?[] Args)> _events = new();

    // Runs before every script: Lua-side helpers that need to call Lua functions (require, inspect, table.*).
    private const string Prelude = """
        -- Host hooks: keep them as locals and take them out of the globals.
        local __load_module, __log = __load_module, __log
        _G.__load_module, _G.__log = nil, nil
        local loaded = {}
        function require(name)
            if type(name) ~= "string" then error("require(name): name must be a string", 2) end
            local cached = loaded[name]
            if cached ~= nil then return cached end
            loaded[name] = false -- a module that requires itself gets false instead of looping
            local result = __load_module(name)(name)
            if result == nil then result = true end
            loaded[name] = result
            return result
        end

        local function inspect(value, indent, seen)
            if type(value) == "string" then return string.format("%q", value) end
            if type(value) ~= "table" then return tostring(value) end
            seen = seen or {}
            if seen[value] then return "<cycle>" end
            seen[value] = true
            indent = indent or ""
            local inner = indent .. "  "
            local keys = {}
            for k in pairs(value) do keys[#keys + 1] = k end
            if #keys == 0 then seen[value] = nil return "{}" end
            if #keys == #value then
                -- A list: {1, 2, 3} on one line when it's short.
                local items = {}
                for i = 1, #value do items[i] = inspect(value[i], inner, seen) end
                seen[value] = nil
                local line = "{" .. table.concat(items, ", ") .. "}"
                if #line <= 80 and not line:find("\n") then return line end
                return "{\n" .. inner .. table.concat(items, ",\n" .. inner) .. "\n" .. indent .. "}"
            end
            table.sort(keys, function(a, b)
                if type(a) == type(b) and (type(a) == "number" or type(a) == "string") then return a < b end
                return type(a) < type(b)
            end)
            local parts = {}
            for _, k in ipairs(keys) do
                local key = type(k) == "string" and k:match("^[%a_][%w_]*$") and k or "[" .. inspect(k, inner, seen) .. "]"
                parts[#parts + 1] = inner .. key .. " = " .. inspect(value[k], inner, seen)
            end
            seen[value] = nil
            return "{\n" .. table.concat(parts, ",\n") .. "\n" .. indent .. "}"
        end
        _G.inspect = function(value) return inspect(value) end

        -- log / print take any number of values; tables are shown with inspect().
        function log(...)
            local parts = {}
            for i = 1, select("#", ...) do
                local v = select(i, ...)
                parts[i] = type(v) == "table" and inspect(v) or tostring(v)
            end
            __log(table.concat(parts, "  "))
        end
        print = log

        function table.keys(t) local r = {} for k in pairs(t) do r[#r + 1] = k end return r end
        function table.values(t) local r = {} for _, v in pairs(t) do r[#r + 1] = v end return r end
        function table.count(t) local n = 0 for _ in pairs(t) do n = n + 1 end return n end
        function table.find(t, value) for k, v in pairs(t) do if v == value then return k end end return nil end
        function table.contains(t, value) return table.find(t, value) ~= nil end
        function table.map(t, fn) local r = {} for k, v in pairs(t) do r[k] = fn(v, k) end return r end
        function table.filter(t, fn) local r = {} for _, v in ipairs(t) do if fn(v) then r[#r + 1] = v end end return r end
        function table.reduce(t, fn, acc) for _, v in ipairs(t) do acc = fn(acc, v) end return acc end
        function table.slice(t, from, to)
            local r = {}
            for i = from or 1, to or #t do r[#r + 1] = t[i] end
            return r
        end
        function table.reverse(t) local r = {} for i = #t, 1, -1 do r[#r + 1] = t[i] end return r end
        function table.merge(...)
            local r = {}
            for i = 1, select("#", ...) do
                local t = select(i, ...)
                if t then for k, v in pairs(t) do r[k] = v end end
            end
            return r
        end
        function table.copy(t, seen)
            if type(t) ~= "table" then return t end
            seen = seen or {}
            if seen[t] then return seen[t] end
            local r = {}
            seen[t] = r
            for k, v in pairs(t) do r[table.copy(k, seen)] = table.copy(v, seen) end
            return setmetatable(r, getmetatable(t))
        end
        function table.is_empty(t) return next(t) == nil end
        """;

    private void RegisterExtras(Instance instance, Table g)
    {
        g["__log"] = (Action<string>)(msg => LuaLog.Write($"[{instance.Name}] {msg}"));
        g["__load_module"] = DynValue.NewCallback((_, a) => LoadModule(instance, Text(a, 0, "")));

        RegisterStorage(instance, g);
        RegisterEvents(instance, g);
        RegisterTasks(instance, g);
        RegisterEncoding(instance, g);

        if (g.Get("http") is { Type: DataType.Table } http)
            http.Table["request"] = DynValue.NewCallback((_, a) =>
            {
                Require(instance, "http");
                Request(instance, a);
                return DynValue.Nil;
            });

        instance.Script.DoString(Prelude, codeFriendlyName: "prelude");
    }

    // ---- require(name): scripts\lib\<name>.lua / .enma first, then scripts\<name>.lua / .enma ----

    private DynValue LoadModule(Instance instance, string name)
    {
        if (name.Length == 0 || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            || name.Contains(".."))
            throw new ScriptRuntimeException($"require: '{name}' isn't a valid module name (letters, digits, _ - . only)");

        // "utils.strings" -> lib\utils\strings.lua
        string relative = name.Replace('.', Path.DirectorySeparatorChar);
        string? path = new[] { Path.Combine("lib", relative), relative }
            .SelectMany(p => new[] { p + ".lua", p + ".enma" })
            .Select(p => Path.Combine(ScriptService.Folder, p))
            .FirstOrDefault(File.Exists);
        if (path == null)
            throw new ScriptRuntimeException($"require: no module '{name}' (looked for scripts\\lib\\{relative}.lua and .enma)");

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScriptRuntimeException($"require: can't read {Path.GetFileName(path)}: {ex.Message}");
        }

        if (string.Equals(Path.GetExtension(path), ".enma", StringComparison.OrdinalIgnoreCase))
        {
            var compiled = Enma.EnmaCompiler.Compile(source);
            if (compiled.Lua is not { } lua)
            {
                var error = compiled.Errors.First();
                throw new ScriptRuntimeException($"require: {name}.enma line {error.Line}: {error.Message}");
            }
            source = lua;
        }
        return instance.Script.LoadString(source, codeFriendlyName: name);
    }

    // ---- storage.*: small values kept between runs, in data\<script>\storage.json (no permission needed) ----

    private static string StoragePath(Instance instance) => Path.Combine(ScriptFiles.Root, instance.Name, "storage.json");

    private static void RegisterStorage(Instance instance, Table g)
    {
        var storage = new Table(instance.Script);
        storage["get"] = DynValue.NewCallback((_, a) =>
        {
            string key = Key(a, "get");
            return Values(instance).TryGetPropertyValue(key, out var node)
                ? FromPlain(instance.Script, FromJson(node))
                : a.Count > 1 ? a[1] : DynValue.Nil;
        });
        storage["set"] = DynValue.NewCallback((_, a) =>
        {
            string key = Key(a, "set");
            var value = a.Count > 1 ? a[1] : DynValue.Nil;
            if (value.IsNil()) Values(instance).Remove(key);
            else Values(instance)[key] = JsonSerializer.SerializeToNode(ToPlain(value, 0, "storage.set"));
            instance.StorageDirty = true;
            return DynValue.Nil;
        });
        storage["delete"] = DynValue.NewCallback((_, a) =>
        {
            instance.StorageDirty |= Values(instance).Remove(Key(a, "delete"));
            return DynValue.Nil;
        });
        storage["keys"] = DynValue.NewCallback((_, _) =>
        {
            var list = new Table(instance.Script);
            foreach (string key in Values(instance).Select(p => p.Key).Order(StringComparer.Ordinal)) list.Append(DynValue.NewString(key));
            return DynValue.NewTable(list);
        });
        storage["clear"] = DynValue.NewCallback((_, _) =>
        {
            Values(instance).Clear();
            instance.StorageDirty = true;
            return DynValue.Nil;
        });
        g["storage"] = storage;

        static string Key(CallbackArguments a, string function) =>
            a.Count > 0 && a[0].Type is DataType.String or DataType.Number
                ? a[0].CastToString()!
                : throw new ScriptRuntimeException($"storage.{function}(key, ...): key must be a string");
    }

    private static JsonObject Values(Instance instance)
    {
        if (instance.Storage != null) return instance.Storage;
        try
        {
            string path = StoragePath(instance);
            instance.Storage = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LuaLog.Write($"[{instance.Name}] storage.json couldn't be read; starting empty ({ex.Message})");
            instance.Storage = [];
        }
        return instance.Storage;
    }

    // Written at most once per tick, and when the script stops.
    private static void SaveStorage(Instance instance)
    {
        if (!instance.StorageDirty || instance.Storage == null) return;
        instance.StorageDirty = false;
        string json = instance.Storage.ToJsonString();
        if (json.Length > MaxStorageBytes)
        {
            LuaLog.Write($"[{instance.Name}] storage is larger than 1 MB and wasn't saved");
            return;
        }
        try
        {
            string path = StoragePath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LuaLog.Write($"[{instance.Name}] storage couldn't be saved: {ex.Message}");
        }
    }

    // ---- events.on(name, fn) / events.emit(name, ...): messages between scripts ----

    private void RegisterEvents(Instance instance, Table g)
    {
        var events = new Table(instance.Script);
        events["on"] = DynValue.NewCallback((_, a) =>
        {
            string name = Text(a, 0, "");
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            if (name.Length == 0 || fn.Type != DataType.Function)
                throw new ScriptRuntimeException("events.on(name, fn): name must be a string and fn a function");
            var entry = new EventHandler(name, fn);
            instance.EventHandlers.Add(entry);
            return UserData.Create(new TimerHandle(() => instance.EventHandlers.Remove(entry),
                                                   () => instance.EventHandlers.Contains(entry) && IsCurrent(instance)));
        });
        events["emit"] = DynValue.NewCallback((_, a) =>
        {
            string name = Text(a, 0, "");
            if (name.Length == 0) throw new ScriptRuntimeException("events.emit(name, ...): name must be a string");
            if (_events.Count >= 1000) throw new ScriptRuntimeException("events.emit: too many events queued at once");
            var args = new object?[Math.Max(a.Count - 1, 0)];
            for (int i = 1; i < a.Count; i++) args[i - 1] = ToPlain(a[i], 0, "events.emit");
            _events.Enqueue((name, args));
            return DynValue.Nil;
        });
        g["events"] = events;
    }

    private sealed record EventHandler(string Name, DynValue Callback);

    private void DeliverEvents()
    {
        int count = _events.Count; // events emitted by handlers wait for the next tick
        for (int i = 0; i < count; i++)
        {
            var (name, args) = _events.Dequeue();
            foreach (var instance in _running.Values.ToArray())
            foreach (var handler in instance.EventHandlers.ToArray())
            {
                if (handler.Name != name || !IsCurrent(instance)) continue;
                // Each script gets its own copy, so one can't change what another received.
                var values = args.Select(v => FromPlain(instance.Script, v)).ToArray();
                if (!SafeCall(instance, handler.Callback, values)) break;
            }
        }
    }

    // ---- spawn(fn, ...) / wait(seconds): code that pauses without blocking apathy ----

    private void RegisterTasks(Instance instance, Table g)
    {
        g["spawn"] = DynValue.NewCallback((_, a) =>
        {
            var fn = a.Count > 0 ? a[0] : DynValue.Nil;
            if (fn.Type != DataType.Function) throw new ScriptRuntimeException("spawn(fn, ...): fn must be a function");
            if (instance.Tasks.Count >= 200) throw new ScriptRuntimeException("spawn: this script already runs 200 tasks");

            var coroutine = instance.Script.CreateCoroutine(fn).Coroutine;
            coroutine.AutoYieldCounter = WatchdogInstructions;
            var task = new LuaTask(coroutine, [.. a.GetArray(1)]) { Due = Uptime.Elapsed.TotalSeconds };
            instance.Tasks.Add(task);
            return UserData.Create(new TimerHandle(() => task.Cancelled = true,
                                                   () => !task.Cancelled && instance.Tasks.Contains(task) && IsCurrent(instance)));
        });
        g["wait"] = DynValue.NewCallback((_, a) =>
        {
            if (!instance.InTask)
                throw new ScriptRuntimeException("wait(seconds) works only inside spawn(function() ... end)");
            return DynValue.NewYieldReq([DynValue.NewNumber(Math.Max(Number(a, 0, 0), 0))]);
        });
    }

    private void RunTasks()
    {
        double now = Uptime.Elapsed.TotalSeconds;
        foreach (var instance in _running.Values.ToArray())
        foreach (var task in instance.Tasks.ToArray())
        {
            if (task.Cancelled)
            {
                instance.Tasks.Remove(task);
                continue;
            }
            if (task.Due > now || !IsCurrent(instance)) continue;
            if (!ResumeTask(instance, task)) break; // script errored and was stopped
        }
    }

    // One step of a task: runs until its next wait() or its end, under the same watchdog as callbacks.
    private bool ResumeTask(Instance instance, LuaTask task)
    {
        var coroutine = task.Coroutine;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        instance.InTask = true;
        try
        {
            DynValue result;
            if (task.StartArgs is { } args)
            {
                task.StartArgs = null;
                result = coroutine.Resume(args);
            }
            else
            {
                result = coroutine.Resume();
            }
            while (coroutine.State == CoroutineState.ForceSuspended)
            {
                if (stopwatch.Elapsed > WatchdogBudget)
                {
                    LuaLog.Write($"[{instance.Name}] stopped: a spawned task ran too long without wait() — check for an endless loop.");
                    NotificationService.Show($"{instance.Name} stopped", "A task ran too long without wait().", NotificationKind.Error);
                    Stop(instance.Name);
                    return false;
                }
                result = coroutine.Resume();
            }

            if (coroutine.State == CoroutineState.Dead) instance.Tasks.Remove(task);
            else task.Due = Uptime.Elapsed.TotalSeconds + (result.Type == DataType.Number ? result.Number : 0);
            return true;
        }
        catch (InterpreterException ex)
        {
            Fail(instance, ex);
            return false;
        }
        finally
        {
            instance.InTask = false;
        }
    }

    // ---- base64.*, hash.*, util.* ----

    private static void RegisterEncoding(Instance instance, Table g)
    {
        var base64 = new Table(instance.Script);
        base64["encode"] = DynValue.NewCallback((_, a) =>
            DynValue.NewString(Convert.ToBase64String(Encoding.UTF8.GetBytes(Text(a, 0, "")))));
        base64["decode"] = DynValue.NewCallback((_, a) =>
        {
            try
            {
                return DynValue.NewString(Encoding.UTF8.GetString(Convert.FromBase64String(Text(a, 0, ""))));
            }
            catch (FormatException)
            {
                return DynValue.Nil;
            }
        });
        g["base64"] = base64;

        var hash = new Table(instance.Script);
        hash["sha256"] = DynValue.NewCallback((_, a) =>
            DynValue.NewString(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text(a, 0, ""))))));
        hash["md5"] = DynValue.NewCallback((_, a) =>
            DynValue.NewString(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Text(a, 0, ""))))));
        hash["hmac_sha256"] = DynValue.NewCallback((_, a) =>
            DynValue.NewString(Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Text(a, 0, "")),
                                                                            Encoding.UTF8.GetBytes(Text(a, 1, ""))))));
        g["hash"] = hash;

        var util = new Table(instance.Script);
        util["uuid"] = (Func<string>)(() => Guid.NewGuid().ToString());
        util["random"] = DynValue.NewCallback((_, a) =>
        {
            // Unpredictable numbers (math.random is fine for games, this one for tokens).
            if (a.Count == 0) return DynValue.NewNumber(RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue);
            int min = (int)Number(a, 0, 1), max = (int)Number(a, 1, min);
            if (a.Count == 1) (min, max) = (1, min);
            if (max < min) throw new ScriptRuntimeException("util.random(min, max): max is smaller than min");
            return DynValue.NewNumber(RandomNumberGenerator.GetInt32(min, max + 1));
        });
        util["url_encode"] = DynValue.NewCallback((_, a) => DynValue.NewString(Uri.EscapeDataString(Text(a, 0, ""))));
        util["url_decode"] = DynValue.NewCallback((_, a) => DynValue.NewString(Uri.UnescapeDataString(Text(a, 0, ""))));
        g["util"] = util;
    }

    // ---- http.request{ url, method, headers, body, content_type }, fn(body, status, err, headers) ----

    private static readonly HashSet<string> HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    private void Request(Instance instance, CallbackArguments a)
    {
        if (a.Count == 0 || a[0].Type != DataType.Table)
            throw new ScriptRuntimeException("http.request{ url = ..., method = ..., headers = {...}, body = ... }: pass a table");
        var options = a[0].Table;
        string url = options.Get("url").CastToString() ?? "";
        string method = (options.Get("method").CastToString() ?? "GET").ToUpperInvariant();
        if (!HttpMethods.Contains(method))
            throw new ScriptRuntimeException($"http.request: method must be one of {string.Join(", ", HttpMethods)}");

        var headers = new List<(string, string)>();
        if (options.Get("headers") is { Type: DataType.Table } table)
            foreach (var pair in table.Table.Pairs)
                if (pair.Key.CastToString() is { } key && pair.Value.CastToString() is { } value) headers.Add((key, value));

        string? body = options.Get("body") is { } b && !b.IsNil() ? b.CastToString() : null;
        string contentType = options.Get("content_type").CastToString() ?? "application/json";
        Send(instance, new System.Net.Http.HttpMethod(method), url, body, contentType, a.Count > 1 ? a[1] : DynValue.Nil, headers);
    }

    // ---- Converting values between scripts and to JSON ----

    // nil, booleans, numbers, strings and tables of them; functions and userdata can't leave a script.
    private static object? ToPlain(DynValue value, int depth, string function)
    {
        if (depth > MaxValueDepth) throw new ScriptRuntimeException($"{function}: the table is nested too deeply (or refers to itself)");
        switch (value.Type)
        {
            case DataType.Nil or DataType.Void: return null;
            case DataType.Boolean: return value.Boolean;
            case DataType.Number: return value.Number;
            case DataType.String: return value.String;
            case DataType.Table:
                var table = value.Table;
                int length = table.Length;
                if (length > 0 && table.Pairs.Count() == length)
                {
                    var list = new List<object?>(length);
                    for (int i = 1; i <= length; i++) list.Add(ToPlain(table.Get(i), depth + 1, function));
                    return list;
                }
                var map = new Dictionary<string, object?>();
                foreach (var pair in table.Pairs)
                    map[pair.Key.CastToString() ?? ""] = ToPlain(pair.Value, depth + 1, function);
                return map;
            default:
                throw new ScriptRuntimeException($"{function}: a {value.Type.ToLuaTypeString()} can't be stored or sent (only nil, booleans, numbers, strings and tables)");
        }
    }

    private static DynValue FromPlain(Script script, object? value) => value switch
    {
        null => DynValue.Nil,
        bool b => DynValue.NewBoolean(b),
        double d => DynValue.NewNumber(d),
        string s => DynValue.NewString(s),
        List<object?> list => DynValue.NewTable(new Table(script, [.. list.Select(v => FromPlain(script, v))])),
        Dictionary<string, object?> map => MapToTable(script, map),
        _ => DynValue.Nil,
    };

    private static DynValue MapToTable(Script script, Dictionary<string, object?> map)
    {
        var table = new Table(script);
        foreach (var (key, v) in map) table[key] = FromPlain(script, v);
        return DynValue.NewTable(table);
    }

    private static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array.Select(FromJson).ToList(),
        JsonObject obj => obj.ToDictionary(p => p.Key, p => FromJson(p.Value)),
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => null,
    };
}
