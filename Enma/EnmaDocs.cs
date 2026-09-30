namespace Apathy.Enma;

/// <summary>The Enma language reference: shown in Enma Studio and given to the AI agents.</summary>
public static class EnmaDocs
{
    public const string Reference =
        """
        Enma (.enma) is a small scripting language that compiles to Lua 5.2 and runs in apathy with the same
        API as Lua scripts (log, ui, hud, timer, time, color, json, keys, game, chat, http, files, ...).
        Generated Lua keeps the Enma line numbers, so runtime errors point at the right line.

        Comments         // line    /* block */      Header: // @permissions game, chat (first lines)
        Variables        let x = 1     let a, b = f()     const LIMIT = 10  (can't be reassigned)
        Assignment       x = 2   x += 1   -= *= /= %=   s ++= "text"    a, b = b, a
        Strings          "hello {name}, you have {count + 1} kills"   (interpolation; \{ for a brace)
                         'raw {not interpolated}'     concat: "a" ++ "b"
        Numbers          1  2.5  0xFF  1_000_000      ** is power, % modulo
        Logic            && || !   (or: and or not)   == != < > <= >=
        Nil handling     value ?? fallback            (fallback only when value is nil)
        Ternary          cond ? a : b
        Pipe             x |> f(y)  ==  f(x, y)       x |> f  ==  f(x)
        Lists            [1, 2, 3]   (1-based like Lua; #list is the length; list[#list + 1] = v appends)
        Maps             {name: "x", "key with space": 1, [expr]: v, shorthand}   m.name  m["key"]
        Functions        fn add(a, b = 1) { return a + b }     fn sq(x) => x * x
                         let f = fn(x) => x * 2      fn(a, b) { ... }      varargs: fn f(...) { }
                         Functions in a block are hoisted: they may call each other in any order
                         (but top-level code only runs a function after its fn statement has run).
        Methods          obj:method(args)  calls with self;   fn obj:method() { self.v }   fn obj.helper() { }
        If               if a { } elif b { } else { }     (also "else if")
        Loops            while cond { }
                         for i in 0..10 { }      (0..9, end exclusive)   for i in 1..=10 { }  (inclusive)
                         for i in 10..0 step -2 { }
                         for v in list { }       for i, v in list { }    for k, v in map { }
                         break, continue
        Classes          class Dog : Animal {
                             fn init(name) { self.name = name }
                             fn speak() { return "{self.name} barks" }
                         }
                         let d = Dog("rex")   d:speak()    Dog.super is the base class
        Event sugar      on tick { }                 == on_tick(fn() { })        (every 50 ms)
                         on unload { }               == on_unload(fn() { })
                         on game "kill" (e) { }      == game.on("kill", fn(e) { })
                         every 1.5 { }  after 3 { }  == timer.every / timer.after
        Return           return a, b   ("return" alone on its line ends the function)
        Statements need no semicolons (optional ;). Blocks always use braces.

        Compiler checks: syntax errors, writes to const, break/continue outside loops (errors);
        unknown names and implicit globals (warnings).
        """;

    /// <summary>A few lines of Enma idioms, used as the example in agent prompts.</summary>
    public const string Example =
        """
        // @name        Kill counter
        // @author      apathy
        // @version     1.0
        // @description Shows kills this session on the HUD.
        // @permissions game
        let show = ui.checkbox("Show on HUD", true)
        let label = hud.label("")
        let kills = 0

        on game "kill" (e) {
            kills += 1
            if e.headshot { notify("Headshot!", "{kills} kills") }
        }

        on tick {
            label:show(show:get())
            label:set("kills: {kills}")
        }
        """;
}
