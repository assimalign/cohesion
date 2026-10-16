# MP4 profile

Content.Mpeg extends Content.Bmff through explicit registry factories. Use
`Mp4Profile.LoadDocument` for an editable tree with typed sample entries and lazy decoder
configuration, then save through `BmffDocument.Save` or `SaveAsync`. Use BMFF's reader and
writer directly for forward-only streams.

See [DESIGN](DESIGN.md) for package boundaries, retained entries and extension-seam gaps.
