namespace Caddis

open System

/// Versions of Caddis releases, e.g. 0.3.1 or 0.4.0-beta.1, as in the changelog.
module ReleaseVersion =

    /// Whether version a is newer than b, e.g. 0.3.0 than 0.2.1 or than 0.3.0-beta.1.
    let isNewer (a: string) (b: string) =
        let numbers (v: string) =
            match Version.TryParse(v.Split('-')[0]) with
            | true, n -> Some n
            | _ -> None
        match numbers a, numbers b with
        | Some x, Some y -> x > y || (x = y && not (a.Contains "-") && b.Contains "-")
        | _ -> a <> b
