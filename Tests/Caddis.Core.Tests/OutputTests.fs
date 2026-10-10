module Caddis.Tests.OutputTests

open System
open System.Text.RegularExpressions
open Xunit
open Caddis
open Caddis.Http

/// The number in a '[... 123 characters omitted ...]' marker, 0 if there is none.
let omitted (text: string) =
    let m = Regex.Match(text, @"\[\.\.\. (\d+) characters omitted \.\.\.\]\n")
    if m.Success then int64 m.Groups[1].Value else 0L

/// The text without its marker line.
let withoutMarker (text: string) =
    Regex.Replace(text, @"\[\.\.\. \d+ characters omitted \.\.\.\]\n", "")

let lines (n: int) = String.concat "" [ for i in 1..n -> $"line {i}\n" ]

[<Fact>]
let ``keeps all text below the limit`` () =
    let b = CaptureBuffer 100
    b.Append "abc"
    Assert.Equal(("abc", false), b.View(0L, 100))
    Assert.Equal(3L, b.Length)
    Assert.False b.Truncated

[<Fact>]
let ``keeps the start and the end of long text`` () =
    let b = CaptureBuffer 200
    for i in 1..500 do
        b.Append $"line %04d{i}\n" // 10 characters, so the kept start and end are whole lines
    Assert.True b.Truncated
    Assert.StartsWith("line 0001\n", b.Text)
    Assert.EndsWith("line 0500\n", b.Text)
    let shown = withoutMarker b.Text
    Assert.Equal(b.Length, int64 shown.Length + omitted b.Text)

[<Fact>]
let ``shortens a view at line ends and counts the omitted characters`` () =
    let b = CaptureBuffer 100_000
    let text = lines 5000
    b.Append text
    let view, shortened = b.View(0L, 1000)
    Assert.True shortened
    Assert.True(view.Length < 1100, $"view has {view.Length} characters")
    Assert.StartsWith("line 1\n", view)
    Assert.EndsWith("line 5000\n", view)
    // cut at line ends: the line before the marker and the one after it are complete
    Assert.Matches(@"\nline \d+\n\[\.\.\. \d+ characters omitted \.\.\.\]\nline \d+\n", view)
    Assert.Equal(int64 text.Length, int64 (withoutMarker view).Length + omitted view)

[<Fact>]
let ``views only the text after an offset`` () =
    let b = CaptureBuffer 100
    b.Append "first\n"
    let offset = b.Length
    b.Append "second\n"
    Assert.Equal(("second\n", false), b.View(offset, 100))

[<Fact>]
let ``marks text dropped before an offset`` () =
    let b = CaptureBuffer 100
    b.Append(String('a', 500))
    let view, shortened = b.View(100L, 1000) // the middle of the text was dropped by the buffer
    Assert.True shortened
    Assert.StartsWith("[... ", view)
    Assert.Equal(400L, int64 (withoutMarker view).Length + omitted view)

[<Fact>]
let ``clips strings at line ends`` () =
    let text = lines 1000
    let clipped = McpView.clip 500 text
    Assert.StartsWith("line 1\n", clipped)
    Assert.EndsWith("line 1000\n", clipped)
    Assert.Equal(int64 text.Length, int64 (withoutMarker clipped).Length + omitted clipped)

[<Fact>]
let ``captures keep small views and scale down large ones`` () =
    Assert.Equal((800, 600), CaptureSize.fit (800, 600) (0, 0))
    Assert.Equal((1280, 720), CaptureSize.fit (2560, 1440) (0, 0))
    Assert.Equal((500, 250), CaptureSize.fit (2000, 1000) (500, 0))
    Assert.Equal((400, 200), CaptureSize.fit (2000, 1000) (0, 200))
    Assert.Equal((300, 200), CaptureSize.fit (100, 100) (300, 200))
