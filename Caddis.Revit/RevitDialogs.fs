namespace Caddis.Revit

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks
open System.Windows.Automation
open Caddis

/// Finds Revit's modal dialog and presses its buttons, from any thread.
/// Revit's UI thread may be busy, so the dialog is found with Win32 functions that send no messages to it:
/// while a modal dialog shows, Revit's main window is disabled, and its last active popup is the dialog on top.
/// Only then is the dialog read with UI Automation, which sends messages to it: the dialog's message loop answers them.
module internal RevitDialogs =

    module private Native =
        [<DllImport("user32.dll")>]
        extern bool IsWindowVisible(nativeint hWnd)

        [<DllImport("user32.dll")>]
        extern bool IsWindowEnabled(nativeint hWnd)

        [<DllImport("user32.dll")>]
        extern nativeint GetLastActivePopup(nativeint hWnd)

        /// Unlike GetWindowText it sends no WM_GETTEXT to the window's thread.
        [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
        extern int InternalGetWindowText(nativeint hWnd, StringBuilder text, int maxCount)

    /// Revit's main window, set at startup.
    let mutable mainWindow = 0n

    /// How long UI Automation may take to read a dialog or press a button.
    let private timeout = TimeSpan.FromSeconds 5.0

    let private idOf (hwnd: nativeint) = $"0x{int64 hwnd:X}"

    let private titleOf (hwnd: nativeint) =
        let sb = StringBuilder 512
        Native.InternalGetWindowText(hwnd, sb, sb.Capacity) |> ignore
        sb.ToString()

    /// The modal dialog on top of Revit's main window, if one is open.
    let private findDialog () : nativeint option =
        if mainWindow = 0n || Native.IsWindowEnabled mainWindow then
            None
        else
            match Native.GetLastActivePopup mainWindow with
            | h when h <> 0n && h <> mainWindow && Native.IsWindowVisible h -> Some h
            | _ -> None

    /// Runs f on a thread pool thread, at most for the timeout.
    let private withTimeout (what: string) (f: unit -> 'T) : 'T =
        let t = Task.Run(Func<'T> f)
        if t.Wait timeout then t.Result
        else raise (TimeoutException $"{what} took longer than {timeout.TotalSeconds} s.")

    /// The enabled buttons of the dialog, without those of its title bar and scroll bars.
    /// e.g. Revit's error dialog has a disabled 'OK' for errors that can't be ignored.
    let private buttonsOf (dialog: AutomationElement) : AutomationElement list =
        let walker = TreeWalker.ControlViewWalker
        dialog.FindAll(TreeScope.Descendants, PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
        |> Seq.cast<AutomationElement>
        |> Seq.filter (fun b ->
            let parent = walker.GetParent b
            b.Current.IsEnabled
            && (isNull parent || (parent.Current.ControlType <> ControlType.TitleBar && parent.Current.ControlType <> ControlType.ScrollBar)))
        |> List.ofSeq

    let private read (hwnd: nativeint) : HostDialog =
        withTimeout "Reading the dialog" (fun () ->
            let dialog = AutomationElement.FromHandle hwnd
            // the text of labels, and of text boxes and list items, e.g. the messages in Revit's error dialog
            let textOf (e: AutomationElement) =
                match e.Current.ControlType with
                | t when t = ControlType.Edit || t = ControlType.Document ->
                    match e.TryGetCurrentPattern ValuePattern.Pattern with
                    | true, (:? ValuePattern as v) -> v.Current.Value
                    | _ -> e.Current.Name
                | _ -> e.Current.Name
            let isText (e: AutomationElement) =
                let t = e.Current.ControlType
                t = ControlType.Text || t = ControlType.Edit || t = ControlType.Document || t = ControlType.ListItem || t = ControlType.DataItem
            let texts =
                dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                |> Seq.cast<AutomationElement>
                |> Seq.filter isText
                |> Seq.map (fun e -> (textOf e).Trim())
                |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                |> Seq.distinct
                |> String.concat " "
            let texts = if texts.Length > 3000 then texts.Substring(0, 3000) + " ..." else texts
            {
                id = idOf hwnd
                title = titleOf hwnd
                text = texts
                buttons = buttonsOf dialog |> List.map (fun b -> b.Current.Name) |> List.filter (String.IsNullOrWhiteSpace >> not) |> List.distinct
            })

    /// The open modal dialog of Revit, if any. If it can't be read, it is listed with its title only.
    let dialogs () : HostDialog list =
        match findDialog () with
        | None -> []
        | Some hwnd ->
            try [ read hwnd ]
            with e -> [ { id = idOf hwnd; title = titleOf hwnd; text = $"(Caddis could not read the dialog: {e.Message})"; buttons = [] } ]

    /// Button names without accelerator marks, e.g. "&Yes" is "Yes".
    let private plain (s: string) = s.Replace("&", "").Trim()

    /// Presses a button of the open dialog, found by its name. An empty dialog id means the open one.
    let press (dialogId: string, button: string) : Result<string, string> =
        match findDialog () with
        | None -> Error "Revit shows no dialog."
        | Some hwnd when not (String.IsNullOrWhiteSpace dialogId) && not (String.Equals(dialogId.Trim(), idOf hwnd, StringComparison.OrdinalIgnoreCase)) ->
            Error $"The dialog '{dialogId}' is not open anymore, the open one is '{titleOf hwnd}' (id {idOf hwnd})."
        | Some hwnd ->
            let title = titleOf hwnd
            try
                withTimeout "Pressing the button" (fun () ->
                    let buttons = buttonsOf (AutomationElement.FromHandle hwnd)
                    let matches (b: AutomationElement) =
                        String.Equals(plain b.Current.Name, plain button, StringComparison.OrdinalIgnoreCase)
                        || String.Equals(b.Current.AutomationId, button, StringComparison.OrdinalIgnoreCase)
                    match buttons |> List.tryFind matches with
                    | None ->
                        let names = buttons |> List.map (fun b -> $"'{b.Current.Name}'") |> String.concat ", "
                        Error $"The dialog '{title}' has no button '{button}'. Its buttons are: {names}."
                    | Some b ->
                        let name = b.Current.Name // the button is gone after pressing it
                        match b.TryGetCurrentPattern InvokePattern.Pattern with
                        | true, (:? InvokePattern as p) ->
                            p.Invoke()
                            Ok $"Pressed '{name}' in the dialog '{title}'."
                        | _ -> Error $"The button '{name}' can't be pressed.")
            with e ->
                Error $"Pressing '{button}' in the dialog '{title}' failed: {e.Message}"
