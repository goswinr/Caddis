namespace Caddis.AutoCAD

open System

/// The macros of caddis_command: a command and its inputs, e.g. '_.LINE 0,0 10,0 ""'.
module internal AutoCADMacro =

    /// Splits a macro into the arguments of Editor.Command, one per input.
    /// Inputs are separated by spaces, unlike in scripts a space is no Enter. An input that starts with a quote ends at the next quote,
    /// e.g. "Wall 1", and "" is an empty input, which is Enter. Other quotes are part of the input, e.g. 2'6" in feet and inches.
    /// Leading ^C, which cancel a running command in menu macros, are left out: no command runs before a macro does.
    let parse (macro: string) : Result<string[], string> =
        let inputs = ResizeArray<string>()
        let s = macro.Trim()
        let mutable i = 0
        let mutable error = None
        while i < s.Length && error.IsNone do
            if Char.IsWhiteSpace s[i] then
                i <- i + 1
            elif s[i] = '"' then
                match s.IndexOf('"', i + 1) with
                | -1 -> error <- Some $"The macro '{macro}' has a quote without its closing quote."
                | close when close + 1 < s.Length && not (Char.IsWhiteSpace s[close + 1]) ->
                    error <- Some $"The macro '{macro}' needs a space after the quoted input {s.Substring(i, close - i + 1)}."
                | close ->
                    inputs.Add(s.Substring(i + 1, close - i - 1))
                    i <- close + 1
            else
                let start = i
                while i < s.Length && not (Char.IsWhiteSpace s[i]) do
                    i <- i + 1
                inputs.Add(s.Substring(start, i - start))
        match error with
        | Some e -> Error e
        | None ->
            if inputs.Count > 0 then
                let mutable first = inputs[0]
                while first.StartsWith("^C", StringComparison.OrdinalIgnoreCase) do
                    first <- first.Substring 2
                if first = "" then inputs.RemoveAt 0 else inputs[0] <- first
            if inputs.Count = 0 || inputs[0] = "" then Error $"The macro '{macro}' has no command name."
            else Ok(inputs.ToArray())

    /// The command name of parsed inputs without the prefixes '_' for its English name and '.' for the built-in one, e.g. "LINE" for "_.LINE".
    let commandName (inputs: string[]) = inputs[0].TrimStart('_', '.').ToUpperInvariant()
