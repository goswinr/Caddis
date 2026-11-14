namespace Caddis.UI

open System.Collections.Generic
open System.Reflection
open System.Xml
open Avalonia.Media
open AvaloniaEdit.Highlighting
open AvaloniaEdit.Highlighting.Xshd

/// Syntax highlighting for the read-only editors of the panel: F# for the Code tab, with the rules in FSharp.xshd,
/// and the section lines, compiler errors and exceptions of the Output tab, with the rules in Output.xshd.
/// The colors are here, one variant for a light and one for a dark background, so that the text is readable in the host's light and dark themes.
module internal Highlighting =

    /// The named colors of FSharp.xshd, on a light and on a dark background.
    let private fsharpColors =
        dict [
            "Comment", ("#707070", "#8A8F98")
            "DocComment", ("#008A00", "#6A9955")
            "CommentMarker", ("#E00000", "#FF6E6E") // TODO, FIXME, HACK
            "String", ("#B31515", "#D69D85")
            "StringEscape", ("#A050CC", "#D7BA7D") // \n, %d, {{
            "Interpolation", ("#000088", "#9CDCFE") // the code in {} of $"..."
            "Keyword", ("#0000DD", "#569CD6")
            "Boolean", ("#008090", "#4EC9B0")
            "Number", ("#C010A0", "#E08FD0")
            "Directive", ("#C05800", "#E0A050") // #r, #load, #if
            "Attribute", ("#8A5A8A", "#C0A0C8")
            "Punctuation", ("#7744EE", "#A99CFF")
        ]

    /// The named colors of Output.xshd, on a light and on a dark background.
    let private outputColors =
        dict [
            "Section", ("#0063B1", "#6CB8F6") // --- stdout ---
            "Error", ("#C42B1C", "#FF8F8A") // compiler errors, exceptions and their stack traces
            "Warning", ("#8A5D00", "#E8B54A") // compiler warnings
        ]

    let private load (resource: string) (colors: IDictionary<string, string * string>) (dark: bool) =
        use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream resource
        use reader = XmlReader.Create stream
        let xshd = HighlightingLoader.LoadXshd reader
        for c in xshd.Elements |> Seq.choose (function :? XshdColor as c -> Some c | _ -> None) do
            let onLight, onDark = colors[c.Name]
            c.Foreground <- SimpleHighlightingBrush(Color.Parse(if dark then onDark else onLight))
        HighlightingLoader.Load(xshd, HighlightingManager.Instance)

    let private fsharpLight = lazy load "Caddis.UI.Resources.FSharp.xshd" fsharpColors false
    let private fsharpDark = lazy load "Caddis.UI.Resources.FSharp.xshd" fsharpColors true
    let private outputLight = lazy load "Caddis.UI.Resources.Output.xshd" outputColors false
    let private outputDark = lazy load "Caddis.UI.Resources.Output.xshd" outputColors true

    /// The F# highlighting of the Code tab, for a light or a dark background.
    let fsharp (isDark: bool) = if isDark then fsharpDark.Force() else fsharpLight.Force()

    /// The highlighting of the Output tab, for a light or a dark background.
    let output (isDark: bool) = if isDark then outputDark.Force() else outputLight.Force()
