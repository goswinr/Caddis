# Caddis for Rhino

An F# interactive host for Rhino 8, controlled through HTTP and MCP.
AI agents like Claude Code, and your own scripts, run F# code and Rhino commands in the Rhino you are working in.
See the [main README](../README.md) for installation, connecting an agent, the MCP tools and the REST endpoints.

## While a run is in progress

All viewports get a dark yellow border while Caddis runs F# code, Rhino commands or an export,
so you can see when something other than you is working in Rhino:

- On the `UI thread` (the default) Rhino is blocked until the run ends. The border tells you why.
- On a `Background thread` Rhino stays usable, and the border warns you that a script may change the document while you work.

The border is drawn on layout pages but not around their details.
Captures with `caddis_capture_viewport`, `RhinoView.CaptureToBitmap` or `ViewCaptureToFile`, and prints, don't include it.

## The Caddis panel

Open it with the command `Caddis` and the option `Panel`, then dock it like any Rhino panel.

| Dark theme, during a run | Light theme, with the settings unfolded |
| --- | --- |
| ![The Caddis panel in Rhino's dark theme, with a run in progress and the code of the first run](Docs/panel-dark.png) | ![The Caddis panel in Rhino's light theme, with the settings and the output of a run](Docs/panel-light.png) |

- The header shows where the server listens, and the state and thread mode of the F# session.
- `Start` / `Stop` the server, `Copy MCP command` to add Caddis to Claude Code, `Copy token` for other clients.
- `Cancel run` stops a run on the background thread, `Reset FSI session` starts a new F# session.
- `Settings` unfolds the thread mode and the options `Start with Rhino`, `Echo output to the command line`,
  `Listen on the network (LAN)` and `Cancel commands in scripts that wait for input`. All buttons and options have tooltips.
- The recent runs are listed newest first, with their id, status, duration and first line of code:
  `r` for F# code, `c` for Rhino commands, `x` for exports.
- `Output` shows the value, stdout, the Rhino command line, F# Interactive's output, errors and files of the selected run.
- `Code` shows its code read-only, with F# highlighting, line numbers and search (Ctrl+F).
  It is formatted with Fantomas, `Format` switches back to the code as it was run, whose line numbers match the errors.

The panel follows Rhino's light, dark or custom theme as soon as it changes.

## Exemplary run

An agent sent these, the panel lists them newest first:

- `r1` adds three twisted towers of 80 floor slabs, via `caddis_eval`:
  <details open><summary>F# code</summary>

  ```fsharp
  open System
  open System.Drawing
  open Rhino.Geometry
  open Rhino.DocObjects

  // Three twisted towers: their floor slabs get smaller and turn a bit more with every floor.
  let floorHeight = 3600.0

  let tower (origin: Point3d) (floors: int) (turns: float) =
      [| for i in 0 .. floors - 1 do
             let t = float i / float (floors - 1)
             let half = 7000.0 * (1.0 - 0.4 * t)
             let plane = Plane(origin + Vector3d(0.0, 0.0, float i * floorHeight), Vector3d.ZAxis)
             let slab = Box(plane, Interval(-half, half), Interval(-half, half), Interval(0.0, 450.0)).ToBrep()
             slab.Rotate(t * turns * 2.0 * Math.PI, Vector3d.ZAxis, origin) |> ignore
             slab, Color.FromArgb(int (40.0 + 200.0 * t), int (160.0 - 50.0 * t), int (210.0 - 160.0 * t)) |]

  let slabs =
      Array.concat [
          tower (Point3d(0.0, 0.0, 0.0)) 36 0.25
          tower (Point3d(30000.0, 6000.0, 0.0)) 26 -0.2
          tower (Point3d(10000.0, 28000.0, 0.0)) 18 0.15
      ]

  // The geometry is computed on the background thread, the document is changed on Rhino's UI thread.
  Caddis.Script.OnUi(fun () ->
      let doc = Rhino.RhinoDoc.ActiveDoc
      for slab, color in slabs do
          let attributes = new ObjectAttributes(ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = color)
          doc.Objects.AddBrep(slab, attributes) |> ignore)

  printfn "Added %d floor slabs." slabs.Length
  ```
  </details>
- `c2` sets up the view with Rhino commands, via `caddis_command`:
  `-_SetActiveViewport Perspective`, `-_MaxViewport`, `-_SetDisplayMode _Viewport=_Active _Mode=_Shaded`, `_Zoom _All _Extents`.
- `r3` computes the volumes of the slabs. It uses `slabs` from `r1`: the values of earlier runs stay available.
- `x4` exports all objects to `towers.obj`, via `caddis_export`.
- `r5` runs for 12 s on the background thread, the viewport screenshot below was taken meanwhile.

This screenshot was taken in Rhino 8.35 on Windows, after the runs listed above.

![Three twisted towers in a Rhino viewport, framed by the yellow border Caddis draws while a run is in progress](Docs/run-border.png)
