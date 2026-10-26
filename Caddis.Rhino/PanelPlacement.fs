namespace Caddis.Rhino

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json.Nodes
open System.Threading.Tasks
open Rhino
open Rhino.UI
open Caddis

/// Windows functions to find and move the floating window of the panel.
module private Win32 =

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Rect =
        val mutable Left: int
        val mutable Top: int
        val mutable Right: int
        val mutable Bottom: int

    [<Literal>]
    let GA_ROOT = 2u

    [<Literal>]
    let SWP_NOZORDER_NOACTIVATE = 0x0014u

    [<Literal>]
    let MONITOR_DEFAULTTONULL = 0u

    [<DllImport("user32.dll")>]
    extern nativeint GetAncestor(nativeint hwnd, uint32 flags)

    [<DllImport("user32.dll")>]
    extern bool IsWindowVisible(nativeint hwnd)

    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(nativeint hwnd, Rect& rect)

    [<DllImport("user32.dll")>]
    extern bool SetWindowPos(nativeint hwnd, nativeint insertAfter, int x, int y, int width, int height, uint32 flags)

    [<DllImport("user32.dll")>]
    extern nativeint MonitorFromRect(Rect& rect, uint32 flags)


/// Whether the Caddis panel is shown, and where. Rhino keeps that in its layout, but after an update of Caddis it may show the panel
/// in another place. So Caddis keeps it too, in its own folder: saved when Rhino closes, and at the next start it moves the panel back
/// where Rhino's layout differs. A docked panel goes back to the tabs of another panel it was docked with, a floating one to its place
/// on the screen, on Windows.
module PanelPlacement =

    [<Literal>]
    let PanelId = "f3b1a864-f96c-4fcc-b7f4-325d0dab262b"

    let private id = Guid PanelId

    /// The native window of the panel's content, set by the panel. None until it is created.
    let mutable internal panelHandle: unit -> nativeint option = fun () -> None

    type private Placement = {
        Visible: bool
        /// Whether it is the selected tab of its dock bar.
        Selected: bool
        /// Another panel in the same dock bar. Windows only.
        Sibling: Guid option
        /// The floating window in screen pixels: left, top, width, height. Windows only.
        Floating: (int * int * int * int) option
        }

    /// Rhino 8 and 9 have their own layouts.
    let private path = Path.Combine(SettingsStore.DefaultFolder, $"panel-rhino{RhinoApp.ExeVersion}.json")

    /// The floating window that holds the panel. None while it is docked or hidden, and on Mac.
    let private floatingWindow () =
        if not (OperatingSystem.IsWindows()) || not (Panels.IsPanelVisible id) then
            None
        else
            match panelHandle () with
            | Some h when h <> 0n ->
                let root = Win32.GetAncestor(h, Win32.GA_ROOT)
                if root <> 0n && root <> RhinoApp.MainWindowHandle() && Win32.IsWindowVisible root then Some root else None
            | _ -> None

    let private bounds (window: nativeint) =
        let mutable r = Win32.Rect()
        if Win32.GetWindowRect(window, &r) then Some(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) else None

    /// Another panel in the dock bar of this one. Windows only.
    let private sibling () =
        match Panels.PanelDockBar id with
        | bar when bar = Guid.Empty -> None
        | bar -> Panels.GetOpenPanelIds() |> Array.tryFind (fun p -> p <> id && Panels.PanelDockBar p = bar)

    let private toJson (p: Placement) =
        Json.obj [
            "visible", Json.ofBool p.Visible
            "selected", Json.ofBool p.Selected
            "sibling", Json.ofOption (string >> Json.ofString) p.Sibling
            "floating", Json.ofOption (fun (x, y, w, h) -> Json.arr [ Json.ofInt x; Json.ofInt y; Json.ofInt w; Json.ofInt h ] :> JsonNode) p.Floating
        ]

    let private ofJson (n: JsonNode) =
        {
            Visible = Json.tryBool "visible" n |> Option.defaultValue false
            Selected = Json.tryBool "selected" n |> Option.defaultValue true
            Sibling =
                Json.tryString "sibling" n
                |> Option.bind (fun s ->
                    match Guid.TryParse s with
                    | true, g -> Some g
                    | _ -> None)
            Floating =
                match Json.prop "floating" n with
                | Some(:? JsonArray as a) when a.Count = 4 -> Some(a[0].GetValue<int>(), a[1].GetValue<int>(), a[2].GetValue<int>(), a[3].GetValue<int>())
                | _ -> None
        }

    let private hidden = { Visible = false; Selected = false; Sibling = None; Floating = None }

    /// The latest place of the panel while it was shown, or hidden if the user closed it. None until the panel was shown.
    let mutable private last: Placement option = None

    /// Takes note of the panel's place while it is shown. Called every second by the panel.
    let shown () =
        try
            if Panels.IsPanelVisible id then
                last <-
                    Some {
                        Visible = true
                        Selected = Panels.IsPanelVisible(id, true)
                        Sibling = sibling ()
                        Floating = floatingWindow () |> Option.bind bounds
                    }
        with _ ->
            ()

    let private later (ms: int) (f: unit -> unit) =
        Task.Delay(ms).ContinueWith(fun (_: Task) -> RhinoApp.InvokeOnUiThread(Action f)) |> ignore

    let private exiting () = RhinoApp.IsClosing || RhinoApp.IsExiting

    /// When Rhino hides or closes the panel. Closed by the user it stays hidden. While Rhino exits it keeps the place noted last:
    /// Rhino hides it before any event of Rhino or Eto lets Caddis see where it is. Rhino also hides it while another tab of
    /// its dock bar is selected, and it still counts as visible for a moment after hiding it, so this looks again a bit later.
    let hiding () =
        if not (exiting ()) then
            later 500 (fun () -> if not (exiting ()) && not (Panels.IsPanelVisible id) then last <- Some hidden)

    /// When Rhino closes.
    let save () =
        try
            let placement = last |> Option.defaultValue hidden
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, toJson placement |> Json.toText)
        with e ->
            RhinoLog.error $"Caddis: saving the place of the panel to {path} failed: {e.Message}"

    /// Floats the panel and moves its window to the saved place, if that is on a screen. Tries again while the panel's window
    /// doesn't exist yet, or while it is docked after asking Rhino to float it.
    let rec private moveTo (left, top, width, height) (floated: bool) (attempts: int) =
        let again floated =
            if attempts > 0 then later 500 (fun () -> moveTo (left, top, width, height) floated (attempts - 1))
        match floatingWindow () with
        | Some window ->
            let mutable target = Win32.Rect()
            target.Left <- left
            target.Top <- top
            target.Right <- left + width
            target.Bottom <- top + height
            if bounds window <> Some(left, top, width, height) && Win32.MonitorFromRect(&target, Win32.MONITOR_DEFAULTTONULL) <> 0n then
                Win32.SetWindowPos(window, 0n, left, top, width, height, Win32.SWP_NOZORDER_NOACTIVATE) |> ignore
        | None when not floated && Panels.IsPanelVisible id && (panelHandle ()).IsSome ->
            // docked by Rhino
            Panels.FloatPanel(id, Panels.FloatPanelMode.Show) |> ignore
            again true
        | None -> again floated

    /// At Rhino's start, once its layout is shown.
    let private restore () =
        try
            if File.Exists path then
                let p = File.ReadAllText path |> Json.parse |> ofJson
                if not p.Visible then
                    if Panels.IsPanelVisible id then Panels.ClosePanel id
                else
                    match p.Sibling with
                    | Some s when Panels.IsPanelVisible s && Panels.PanelDockBar s <> Panels.PanelDockBar id ->
                        Panels.OpenPanelAsSibling(id, s, p.Selected) |> ignore
                    | _ -> if not (Panels.IsPanelVisible id) then Panels.OpenPanel(id, p.Selected) |> ignore
                    if OperatingSystem.IsWindows() then
                        p.Floating |> Option.iter (fun rect -> moveTo rect false 10)
        with e ->
            RhinoLog.error $"Caddis: restoring the place of the panel from {path} failed: {e.Message}"

    /// Restores the panel a second after Rhino's first idle, when its layout is shown.
    let restoreAtStart () =
        let onIdle = ref null
        onIdle.Value <-
            EventHandler(fun _ _ ->
                RhinoApp.Idle.RemoveHandler onIdle.Value
                later 1000 restore)
        RhinoApp.Idle.AddHandler onIdle.Value
