module Caddis.Tests.ReleaseVersionTests

open Xunit
open Caddis

[<Theory>]
[<InlineData("0.3.1", "0.3.0")>]
[<InlineData("0.10.0", "0.9.9")>]
[<InlineData("1.0.0", "0.99.0")>]
[<InlineData("0.3.0", "0.3.0-beta.1")>]
[<InlineData("0.3.1-beta.1", "0.3.0")>]
let ``a newer version is newer`` (a: string) (b: string) =
    Assert.True(ReleaseVersion.isNewer a b)
    Assert.False(ReleaseVersion.isNewer b a)

[<Theory>]
[<InlineData("0.3.1")>]
[<InlineData("0.3.0-beta.1")>]
let ``a version is not newer than itself`` (v: string) = Assert.False(ReleaseVersion.isNewer v v)
