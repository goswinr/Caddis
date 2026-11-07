namespace Caddis.AutoCAD.Bootstrapper

open System
open System.Diagnostics
open System.Runtime.InteropServices

/// Native task dialogs, with a footer 'What is this?' that links to the repository: the updater shows them while AutoCAD starts,
/// from a process of its own, so the user may not know where they come from. .NET Framework has no managed TaskDialog,
/// and a MessageBox has no footer. TaskDialogIndirect needs version 6 of the common controls, see app.manifest.
/// A copy of Caddis.Revit.Bootstrapper/Dialog.fs with its own title.
module Dialog =

    let private repo = "https://github.com/goswinr/Caddis"

    /// TaskDialogCallbackProc of comctl32.
    type private Callback = delegate of hwnd: nativeint * msg: uint32 * wParam: nativeint * lParam: nativeint * refData: nativeint -> int

    /// TASKDIALOGCONFIG of commctrl.h, which packs it with 1 byte also on x64. The icons are the ids of the predefined ones.
    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)>]
    type private Config =
        val mutable cbSize: uint32
        val mutable hwndParent: nativeint
        val mutable hInstance: nativeint
        val mutable dwFlags: int
        val mutable dwCommonButtons: int
        val mutable pszWindowTitle: string
        val mutable pszMainIcon: nativeint
        val mutable pszMainInstruction: string
        val mutable pszContent: string
        val mutable cButtons: uint32
        val mutable pButtons: nativeint
        val mutable nDefaultButton: int
        val mutable cRadioButtons: uint32
        val mutable pRadioButtons: nativeint
        val mutable nDefaultRadioButton: int
        val mutable pszVerificationText: string
        val mutable pszExpandedInformation: string
        val mutable pszExpandedControlText: string
        val mutable pszCollapsedControlText: string
        val mutable pszFooterIcon: nativeint
        val mutable pszFooter: string
        val mutable pfCallback: Callback
        val mutable lpCallbackData: nativeint
        val mutable cxWidth: uint32

    [<DllImport("comctl32.dll", CharSet = CharSet.Unicode)>]
    extern int private TaskDialogIndirect([<In>] Config& config, int& button, nativeint radioButton, nativeint verificationChecked)

    let private enableHyperlinks = 0x0001 // TDF_ENABLE_HYPERLINKS
    let private allowCancellation = 0x0008 // TDF_ALLOW_DIALOG_CANCELLATION, Esc and the close button
    let private okButton = 0x0001 // TDCBF_OK_BUTTON
    let private yesButton = 0x0002 // TDCBF_YES_BUTTON
    let private noButton = 0x0004 // TDCBF_NO_BUTTON
    let private idYes = 6
    let private errorIcon = nativeint 0xFFFE // TD_ERROR_ICON
    let private infoIcon = nativeint 0xFFFD // TD_INFORMATION_ICON
    let private hyperlinkClicked = 3u // TDN_HYPERLINK_CLICKED, lParam is the href

    /// Opens a clicked link in the browser. A static field, so that it isn't collected while a dialog shows.
    let private onLink =
        Callback(fun _ msg _ lParam _ ->
            if msg = hyperlinkClicked then
                try Process.Start(ProcessStartInfo(Marshal.PtrToStringUni lParam, UseShellExecute = true)) |> ignore
                with _ -> ()
            0)

    /// Shows the text with the buttons and returns the id of the one clicked, or of Cancel for Esc and the close button.
    let private show (text: string) (buttons: int) (icon: nativeint) =
        let mutable config = Config()
        config.cbSize <- uint32 (Marshal.SizeOf<Config>())
        config.dwFlags <- enableHyperlinks ||| allowCancellation
        config.dwCommonButtons <- buttons
        config.pszWindowTitle <- "Caddis for AutoCAD"
        config.pszMainIcon <- icon
        config.pszContent <- text
        config.pszFooterIcon <- infoIcon
        config.pszFooter <- $"<a href=\"{repo}\">What is this?</a>"
        config.pfCallback <- onLink
        let mutable button = 0
        Marshal.ThrowExceptionForHR(TaskDialogIndirect(&config, &button, 0n, 0n))
        button

    /// Asks with Yes and No buttons. True for Yes.
    let ask (text: string) = show text (yesButton ||| noButton) infoIcon = idYes

    /// Shows a message with an OK button.
    let info (text: string) = show text okButton infoIcon |> ignore

    /// Shows an error with an OK button.
    let error (text: string) = show text okButton errorIcon |> ignore
