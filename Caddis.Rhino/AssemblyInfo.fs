namespace Caddis.Rhino

open System.Runtime.InteropServices
open Rhino.PlugIns

// Rhino identifies the plugin by this GUID. Don't change it.
[<assembly: Guid("5507c750-f992-4009-b079-263a062279fb")>]
[<assembly: ComVisible(false)>]
[<assembly: PlugInDescription(DescriptionType.Organization, "Studio Rothenthal")>]
[<assembly: PlugInDescription(DescriptionType.Email, "goswin@rothenthal.com")>]
[<assembly: PlugInDescription(DescriptionType.Country, "Austria")>]
[<assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/goswinr/Caddis")>]
do ()
