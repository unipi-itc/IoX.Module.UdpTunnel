module IoX.Module.UdpTunnel.Utils

open System.Collections
open System.Collections.Generic
open System.IO
open Ionic.Zlib

type PacketStats = {
  Count: int64
  Bytes: int64
}

type InOutStats = {
  Incoming: PacketStats
  Outgoing: PacketStats
}

let noPackets = { Count = 0L; Bytes = 0L }
let addPackets (stats: byref<_>) count bytes =
  stats <- {
    stats with
      Count = stats.Count + count
      Bytes = stats.Bytes + bytes
  }

[<AllowNullLiteral>]
type Compressor(stream: Stream) =
  let deflateStream = new DeflateStream(stream, CompressionMode.Compress, CompressionLevel.BestCompression)
  let writer = new BinaryWriter(deflateStream)
  let mutable totalOut = 0L

  member this.TotalOut = totalOut

  member this.SyncWriteMessage(msg: byte[]) =
    writer.Write(msg.Length)
    deflateStream.FlushMode <- FlushType.Sync
    writer.Write(msg)
    writer.Flush()
    deflateStream.FlushMode <- FlushType.None
    totalOut <- deflateStream.TotalOut

  member this.BufferedWriteMessage(msg: byte[]) =
    writer.Write(msg.Length)
    writer.Write(msg)
    totalOut <- deflateStream.TotalOut

  member this.Dispose() = writer.Dispose()

  interface System.IDisposable with
    member this.Dispose() = this.Dispose()

let compressedMsg msg =
  use stream = new MemoryStream()
  do
    use c = new Compressor(stream)
    c.BufferedWriteMessage(msg)
  stream.ToArray()

type DecompressEnumerator(compressed) =
  let stream = new DeflateStream(compressed, CompressionMode.Decompress)
  let reader = new BinaryReader(stream)
  let mutable count = 0
  let mutable msg = [| |]
  interface IEnumerator with
    member this.Current = msg :> _
    member this.MoveNext() =
      try
        count <- reader.ReadInt32()
        msg <- reader.ReadBytes(count)
        if msg.Length <> count then
          failwithf "Malformed message: expected %A bytes but only received %A" count msg.Length
        true
      with
      | :? EndOfStreamException -> false
      | e ->
        count <- -1
        msg <- [| |]
        raise e
    member this.Reset() = System.NotSupportedException() |> raise
  interface IEnumerator<byte[]> with
    member this.Current = msg
    member this.Dispose() =
      reader.Dispose()
      stream.Dispose()

let decompress compressed =
  { new IEnumerable<_> with
      member this.GetEnumerator() = new DecompressEnumerator(compressed) :> _
    interface IEnumerable with
      member this.GetEnumerator() = new DecompressEnumerator(compressed) :> _
  }

let asString msg =
  try System.Text.Encoding.UTF8.GetString(msg) with _ -> "[ binary message ]"
