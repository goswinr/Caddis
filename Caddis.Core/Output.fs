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

/// Collects text up to a maximum number of characters. Thread safe.
type CaptureBuffer(maxChars: int) =
    let sb = StringBuilder()
    let mutable truncated = false

    member _.Append(s: string) =
        if not (String.IsNullOrEmpty s) then
            lock sb (fun () ->
                let room = maxChars - sb.Length
                if s.Length <= room then
                    sb.Append s |> ignore
                else
                    if room > 0 then sb.Append(s, 0, room) |> ignore
                    truncated <- true)

    member _.Text = lock sb (fun () -> sb.ToString())

    /// True if text was dropped because the maximum size was reached.
    member _.Truncated = truncated


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
