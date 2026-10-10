namespace Caddis

open System
open System.IO
open System.Text

/// The output streams captured for each run.
type OutputKind =
    /// Console.Out, e.g. printfn
    | StdOut
    /// Console.Error, e.g. eprintfn
    | StdErr
    /// The output of F# Interactive itself, e.g. "val x: int = 1"
    | FsiOut

/// Helpers to shorten text by keeping its start and end, cut at line ends where possible.
module internal Clip =

    /// The first 'n' characters, cut back to the end of a line if that keeps at least half of them.
    let head (n: int) (s: string) =
        let h = s.Substring(0, min n s.Length)
        let i = h.LastIndexOf '\n'
        if h.Length < s.Length && i >= h.Length / 2 then h.Substring(0, i + 1) else h

    /// The last 'n' characters, cut forward to the start of a line if that keeps at least half of them.
    let tail (n: int) (s: string) =
        let t = s.Substring(s.Length - min n s.Length)
        let i = t.IndexOf '\n'
        if t.Length < s.Length && i >= 0 && i < t.Length / 2 then t.Substring(i + 1) else t

    /// A line that says how many characters were left out here.
    let marker (before: string) (omitted: int64) =
        let sep = if before = "" || before.EndsWith "\n" then "" else "\n"
        $"{sep}[... {omitted} characters omitted ...]\n"


/// Collects text up to a maximum number of characters. Thread safe.
/// If more text arrives, the first half and the most recent half are kept and the middle is dropped,
/// because errors and results are usually at the end.
type CaptureBuffer(maxChars: int) =
    let sync = obj ()
    let headMax = maxChars / 2
    let tailMax = maxChars - headMax
    /// All text until it overflows, then the first headMax characters.
    let head = StringBuilder()
    /// After the overflow: the latest characters, between tailMax and 2 * tailMax of them.
    let tail = StringBuilder()
    let mutable overflowed = false
    let mutable total = 0L

    let trimTail () =
        if tail.Length > 2 * tailMax then tail.Remove(0, tail.Length - tailMax) |> ignore

    member _.Append(s: string) =
        if not (String.IsNullOrEmpty s) then
            lock sync (fun () ->
                total <- total + int64 s.Length
                if overflowed then
                    tail.Append s |> ignore
                    trimTail ()
                elif head.Length + s.Length <= maxChars then
                    head.Append s |> ignore
                else
                    overflowed <- true
                    head.Append s |> ignore
                    tail.Append(head.ToString(headMax, head.Length - headMax)) |> ignore
                    head.Length <- headMax
                    trimTail ())

    /// The number of characters appended so far, including dropped ones.
    member _.Length = total

    /// True if text was dropped because the maximum size was reached.
    member _.Truncated = overflowed

    /// The text from the character offset 'from' on, shortened to about maxChars by keeping its start and end.
    /// A marker line shows where characters were left out. Returns the text and true if anything was left out.
    member _.View(from: int64, maxChars: int) : string * bool =
        lock sync (fun () ->
            let from = max 0L (min from total)
            let headStr = head.ToString()
            let tailStr = tail.ToString() // empty if not overflowed
            let tailStart = total - int64 tailStr.Length
            // The retained text of the requested range in one or two parts.
            // 'skipped' dropped characters come before the first part, 'gap' ones between the two parts.
            let skipped, first, last =
                if from < int64 headStr.Length then
                    0L, headStr.Substring(int from), tailStr
                else
                    let start = max from tailStart
                    start - from, tailStr.Substring(int (start - tailStart)), ""
            let gap = if last = "" then 0L else tailStart - int64 headStr.Length
            if skipped = 0L && gap = 0L && first.Length + last.Length <= maxChars then
                first + last, false
            else
                let headPart = Clip.head (int (int64 maxChars * 2L / 5L)) first
                let tailPart =
                    let rest = maxChars - headPart.Length
                    if last = "" then Clip.tail (min rest (first.Length - headPart.Length)) first // don't overlap the head part
                    else Clip.tail rest last
                let omitted = total - from - skipped - int64 headPart.Length - int64 tailPart.Length
                let prefix = if skipped > 0L then Clip.marker "" skipped else ""
                let middle = if omitted > 0L then Clip.marker headPart omitted else ""
                prefix + headPart + middle + tailPart, true)

    /// All retained text, with a marker where text was dropped.
    member this.Text = this.View(0L, Int32.MaxValue) |> fst


/// A TextWriter that writes to the buffer of the current run, if any,
/// and passes all text to an echo function, e.g. for the Rhino command line.
/// The same instances are used for all runs, because the FSI session keeps the writers it was created with.
type RoutingWriter(kind: OutputKind, echo: OutputKind -> string -> unit) =
    inherit TextWriter()

    [<VolatileField>]
    let mutable target: CaptureBuffer option = None

    let write (s: string) =
        match target with
        | Some b -> b.Append s
        | None -> ()
        try echo kind s with _ -> ()

    /// The buffer of the current run.
    member _.Target
        with get () = target
        and set v = target <- v

    override _.Encoding = Encoding.UTF8

    override _.Write(c: char) = write (string c)

    override _.Write(s: string) = if not (isNull s) then write s

    override _.Write(buffer: char[], index: int, count: int) = write (String(buffer, index, count))
