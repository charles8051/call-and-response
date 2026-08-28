using System.IO.Pipelines;

namespace CallAndResponse.Transport.BleNordicUart;

/// <summary>
/// An <see cref="IDuplexPipe"/> for BLE Nordic UART communication.
/// The caller owns the BLE connection, notification subscription, and the
/// background loop that drains TX bytes to the characteristic.
/// This class simply pairs the two pipe ends.
/// </summary>
public sealed class BleNordicUartPipe : IDuplexPipe
{
    /// <inheritdoc />
    public PipeReader Input { get; }

    /// <inheritdoc />
    public PipeWriter Output { get; }

    /// <summary>
    /// The writer that the caller's notification handler should push received bytes into.
    /// </summary>
    public PipeWriter RxWriter { get; }

    /// <summary>
    /// The reader that the caller's TX drain loop should read from and write to the characteristic.
    /// </summary>
    public PipeReader TxReader { get; }

    public BleNordicUartPipe()
    {
        var rxPipe = new Pipe();
        var txPipe = new Pipe();

        Input = rxPipe.Reader;
        RxWriter = rxPipe.Writer;

        Output = txPipe.Writer;
        TxReader = txPipe.Reader;
    }
}
