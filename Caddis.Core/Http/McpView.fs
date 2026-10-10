/// How results are shown to MCP clients: as text written for the model, plus images.
/// Unlike the REST responses, empty fields are left out and long output is shortened, to save the model's context.
/// There is no structuredContent: Claude Code shows the model only the structuredContent if a result has one,
/// so the text would be replaced by JSON. see https://github.com/anthropics/claude-code/issues/55677
module Caddis.Http.McpView

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Caddis

// The most characters shown per part of a result. Longer output is saved to a file.
let maxStdOut = 12_000
let maxStdErr = 4_000
let maxFsiOut = 6_000
let maxValue = 4_000
let maxException = 4_000
let maxDiagnostics = 20

/// Images saved by a run are returned inline, up to this many and this size each.
let maxImages = 4
let maxImageBytes = 3_500_000L

let private normalize (s: string) = s.Replace("\r\n", "\n").TrimEnd()

/// Joins lines and runs of white space, e.g. in compiler messages.
let private collapse (s: string) = Regex.Replace(s.Trim(), @"\s+", " ")

/// Shortens a text to about maxChars by keeping its start and end.
let clip (maxChars: int) (s: string) =
    if s.Length <= maxChars then
        s
    else
        let head = Clip.head (maxChars * 2 / 5) s
        let tail = Clip.tail (maxChars - head.Length) s
        head + Clip.marker head (int64 (s.Length - head.Length - tail.Length)) + tail

let private formatSize (bytes: int64) =
    if bytes < 1024L then $"{bytes} B"
    elif bytes < 1024L * 1024L then $"%.1f{float bytes / 1024.0} KB"
    else $"%.1f{float bytes / 1048576.0} MB"

/// Only errors and warnings, not info or hidden diagnostics.
let private relevant (diagnostics: Diagnostic[]) =
    diagnostics |> Array.filter (fun d -> d.severity = "error" || d.severity = "warning")

/// One line per diagnostic. The file only if it is not the evaluated script itself.
let private diagnosticLines (scriptPath: string) (diagnostics: Diagnostic[]) : string list = [
    for d in Array.truncate maxDiagnostics diagnostics do
        let file =
            if String.IsNullOrEmpty d.file || String.Equals(d.file, scriptPath, StringComparison.OrdinalIgnoreCase) then ""
            else $" in {d.file}"
        $"{d.severity} FS%04d{d.errorNumber} at {d.startLine}:{d.startColumn}-{d.endLine}:{d.endColumn}{file}: {collapse d.message}"
    if diagnostics.Length > maxDiagnostics then
        $"... and {diagnostics.Length - maxDiagnostics} more"
]

/// The exception text without the stack frames that don't help: the frames after the last one in the script,
/// and all but the first few frames before the first one in the script. If no frame is in the script, the first few.
/// The full path of the script is shortened to its file name.
let exceptionText (scriptPath: string) (x: ExceptionInfo) : string =
    let fileName = if String.IsNullOrEmpty scriptPath then "" else Path.GetFileName scriptPath
    let inScript (l: string) = fileName <> "" && l.Contains(fileName + ":line ", StringComparison.OrdinalIgnoreCase)
    let isFrame (l: string) = l.StartsWith "   at "
    let lines = (normalize x.text).Split('\n')
    let out = ResizeArray<string>()
    let mutable i = 0
    while i < lines.Length do
        if isFrame lines[i] then
            let start = i
            while i < lines.Length && isFrame lines[i] do
                i <- i + 1
            let frames = lines[start .. i - 1]
            match Array.tryFindIndex inScript frames, Array.tryFindIndexBack inScript frames with
            | Some first, Some last when first > 6 ->
                out.AddRange frames[0..4]
                out.Add $"   ... {first - 5} more frames"
                out.AddRange frames[first..last]
            | Some _, Some last -> out.AddRange frames[0..last]
            | _ ->
                out.AddRange(Array.truncate 5 frames)
                if frames.Length > 5 then out.Add $"   ... {frames.Length - 5} more frames"
        else
            if not (lines[i].Contains "--- End of stack trace from previous location") then out.Add lines[i]
            i <- i + 1
    let text = String.concat "\n" out
    let text = if String.IsNullOrEmpty scriptPath then text else text.Replace(scriptPath, fileName)
    clip maxException text

let private isLocal (baseUrl: string) =
    baseUrl.Contains "://127.0.0.1" || baseUrl.Contains "://localhost" || baseUrl.Contains "://[::1]"

/// One line per file. The URL only for remote clients, a local one reads the path.
let private fileLines (baseUrl: string) (id: string) (shownInline: FileItem list) (files: FileItem seq) : string list = [
    for f in files do
        let url = if isLocal baseUrl then "" else $", {FileItem.url baseUrl id f}"
        let shown = if List.contains f shownInline then ", shown below" else ""
        $"{f.name} ({formatSize f.size}, {f.mime}{shown}): {f.path}{url}"
]

/// The images among the files that are returned inline: png, jpeg, gif or webp, up to maxImages, each up to maxImageBytes.
let private inlineImages (files: FileItem seq) : (FileItem * JsonNode) list =
    files
    |> Seq.filter (fun f ->
        f.size <= maxImageBytes
        && (f.mime = "image/png" || f.mime = "image/jpeg" || f.mime = "image/gif" || f.mime = "image/webp"))
    |> Seq.choose (fun f ->
        try
            let data = Convert.ToBase64String(File.ReadAllBytes f.path)
            Some(f, Json.obj [ "type", Json.ofString "image"; "data", Json.ofString data; "mimeType", Json.ofString f.mime ] :> JsonNode)
        with _ ->
            None)
    |> Seq.truncate maxImages
    |> List.ofSeq

/// An image content item for a tools/call result.
let image (f: FileItem) : JsonNode =
    match inlineImages [ f ] with
    | [ _, img ] -> img
    | _ -> failwith $"The image '{f.path}' could not be read or is larger than {formatSize maxImageBytes}."

/// A run as text for the model, and the images it saved as image content items.
/// stdoutFrom: only show stdout from this character offset on, e.g. to poll a running run for new output.
let run (runs: RunRegistry) (baseUrl: string) (notes: string list) (stdoutFrom: int64) (r: Run) : string * JsonNode list =
    let o = r.Outcome
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let section (title: string) (body: string) =
        if body <> "" then
            line $"--- {title} ---"
            line body

    // shortened output is saved to a file, so the model can read all of it if needed
    let saved = ResizeArray<string * FileItem>()
    let stream (name: string) (buffer: CaptureBuffer) (from: int64) (maxChars: int) =
        let text, shortened = buffer.View(from, maxChars)
        if shortened then
            try saved.Add(name, runs.SaveLog(r.Id, name, buffer.Text)) with _ -> ()
        normalize text

    let fsiOut =
        stream "fsi" r.FsiOut 0L maxFsiOut
        |> _.Split('\n')
        |> Array.filter (fun l -> l <> "Stopped due to error") // FSI's note after a compile error, the diagnostics say more
        |> String.concat "\n"
        |> normalize
    let stdout = stream "stdout" r.StdOut stdoutFrom maxStdOut
    let stderr = stream "stderr" r.StdErr 0L maxStdErr
    let images = inlineImages r.Files

    if r.IsFinished then
        let undo = if r.UndoSerial.IsSome then ", its changes can be undone with caddis_undo" else ""
        line $"Run {r.Id}: {RunStatus.toString r.Status} after {int64 r.Duration.TotalMilliseconds} ms{undo}."
    else
        line $"Run {r.Id}: still running after %.1f{r.Duration.TotalSeconds} s."
        if r.Mode = Background then
            line $"Wait for the result with caddis_get_run (runId '{r.Id}'), or stop the run with caddis_cancel."
        else
            line $"Wait for the result with caddis_get_run (runId '{r.Id}'). It runs on the UI thread, so it can't be cancelled and Rhino is blocked until it finishes."
    o.message |> Option.iter line
    for n in notes do line n
    match o.value with // FSI's output shows the value too, as 'val it: ...'
    | Some(t, v) when not (fsiOut.Contains "val it") -> section $"value: {t}" (clip maxValue v)
    | _ -> ()
    section "F# Interactive" fsiOut
    section (if stdoutFrom > 0L then $"stdout from character {stdoutFrom}" else "stdout") stdout
    section "stderr" stderr
    section "diagnostics" (diagnosticLines r.ScriptPath (relevant o.diagnostics) |> String.concat "\n")
    o.exn |> Option.iter (fun x -> section "exception" (exceptionText r.ScriptPath x))
    section "files" (fileLines baseUrl r.Id (List.map fst images) r.Files |> String.concat "\n")
    for name, f in saved do
        line $"The {name} output was shortened, all captured {name} output is in {f.path}"
    if not r.IsFinished && r.StdOut.Length > 0L then
        line $"stdout has {r.StdOut.Length} characters so far, pass stdoutOffset {r.StdOut.Length} to caddis_get_run to get only newer output."
    normalize (sb.ToString()), List.map snd images

/// The result of a type-check.
let check (diagnostics: Diagnostic[]) : string =
    match relevant diagnostics with
    | [||] -> "No errors or warnings."
    | shown -> diagnosticLines "" shown |> String.concat "\n"

/// Files of an export or capture, one per line after the title.
let files (baseUrl: string) (id: string) (title: string) (items: FileItem seq) : string =
    title + "\n" + (fileLines baseUrl id [] items |> String.concat "\n")
