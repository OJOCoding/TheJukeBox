namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Reads an arbitrary window of bytes out of a remote object.
/// </summary>
/// <remarks>
/// The single seam between <see cref="DriveRandomAccessStream"/> and the network. It
/// exists so the stream's paging, caching, and request budget can be tested against a
/// fake that counts calls, without an HTTP stack or a Google account. That test is the
/// point: field correctness can be asserted many ways, but only a call count catches the
/// pipeline quietly regressing to whole-file downloads.
/// </remarks>
internal interface IRangeReader
{
    /// <summary>
    /// Fills <paramref name="destination"/> from <paramref name="offset"/>, issuing one
    /// request.
    /// </summary>
    /// <returns>
    /// The number of bytes written, which is short only at the end of the object.
    /// </returns>
    Task<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}
