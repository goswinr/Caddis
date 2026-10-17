namespace Caddis.UI

open System
open Avalonia.Media
open Caddis

/// Colors supplied by the application that hosts the panel.
type PanelPalette = {
    IsDark: bool
    Colors: (string * Color) list
    }

/// Host services used by the shared view. Callbacks run on the host UI thread;
/// Revit document operations still go through its existing external-event service.
type PanelHost = {
    Name: string
    ModeTip: string
    CommandLineTitle: string option
    ChangeNoun: string
    Server: unit -> CaddisServer option
    Start: unit -> unit
    Stop: unit -> unit
    /// Puts the text on the clipboard. False if that failed, e.g. because another program has the clipboard open.
    SetClipboard: string -> bool
    SetMode: ThreadMode -> unit
    SetAutoStart: bool -> unit
    SetEcho: (bool -> unit) option
    SetCancelWaitingCommands: (bool -> unit) option
    Cancel: unit -> unit
    Reset: unit -> unit
    OpenUrl: string -> unit
    /// Shows the host's dialog to save an F# script, with a title and a suggested file name. The chosen path, None if cancelled.
    PickScriptPath: string -> string -> string option
    Message: (unit -> string) option
    /// The host's internal errors. The panel shows a button for them while there are any.
    Errors: ErrorLog
    Refreshed: unit -> unit
    Palette: unit -> PanelPalette
    SubscribeTheme: (unit -> unit) -> IDisposable
    }
