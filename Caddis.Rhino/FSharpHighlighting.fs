namespace Caddis.Rhino

open System.Reflection
open System.Xml
open Avalonia.Media
open AvaloniaEdit.Highlighting
open AvaloniaEdit.Highlighting.Xshd

/// F# syntax highlighting for the code view of the panel. The rules are in FSharp.xshd, the colors here,
/// one variant for a light and one for a dark background, so that the code is readable in Rhino's light and dark themes.
module internal FSharpHighlighting =

    /// The named colors of FSharp.xshd, on a light and on a dark background.
    let private colors =
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

    let private load (dark: bool) =
        use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream "Caddis.Rhino.Resources.FSharp.xshd"
        use reader = XmlReader.Create stream
        let xshd = HighlightingLoader.LoadXshd reader
        for c in xshd.Elements |> Seq.choose (function :? XshdColor as c -> Some c | _ -> None) do
            let onLight, onDark = colors[c.Name]
            c.Foreground <- SimpleHighlightingBrush(Color.Parse(if dark then onDark else onLight))
        HighlightingLoader.Load(xshd, HighlightingManager.Instance)

    let private light = lazy load false
    let private dark = lazy load true

    /// The highlighting for a light or a dark background.
    let get (isDark: bool) = if isDark then dark.Force() else light.Force()
