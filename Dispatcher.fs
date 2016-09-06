namespace IoX.Module.UdpTunnel

open EvReact.Expr
open IoX.Modules
open Suave.EvReact

open Utils

type DispatcherConfiguration = {
  DestinationHost: string
  DestinationPort: int
  ReadOnly: bool
  Verbose: bool
}

type DispatcherStats = {
  ElapsedMS: int64
  Total: InOutStats
}

[<Module(
  Name = "UDP datagrams dispatcher",
  Description = "IoX module that decompresses and dispatches datagrams from an IoX UDP collector."
)>]
type DispatcherModule(data: IModuleData<DispatcherConfiguration>) as this =
  inherit DriverModule()

  // Stats
  let stopwatch = System.Diagnostics.Stopwatch()
  let mutable statsIncoming = noPackets
  let mutable statsOutgoing = noPackets

  let stats (ctx: MsgRequestEventArgs<_>) =
    ctx.Result <- this.BuildJsonReply {
      ElapsedMS = stopwatch.ElapsedMilliseconds
      Total = id {
        Incoming = statsIncoming
        Outgoing = statsOutgoing
      }
    }

  // Message handling
  let socket = new System.Net.Sockets.UdpClient()

  let sendMessage msg =
    socket.Send(
      msg,
      msg.Length,
      data.Configuration.Data.DestinationHost,
      data.Configuration.Data.DestinationPort)
    |> ignore
    addPackets &statsOutgoing 1L msg.LongLength
    if data.Configuration.Data.Verbose then
      printfn "Dispatched message of %A bytes: %s" msg.Length (asString msg)

  let sendData (ctx: HttpEventArgs) =
    ctx.Result <- Suave.Successful.OK ""
    let compressed = ctx.Context.request.rawForm
    if data.Configuration.Data.Verbose then
      printfn "Received %A bytes of compressed data" compressed.LongLength
    addPackets &statsIncoming 1L compressed.LongLength
    use compressedStream = new System.IO.MemoryStream(compressed)
    for msg in decompress compressedStream do
      sendMessage msg

  let updateConfig (ctx:MsgRequestEventArgs<_>) =
    if not data.Configuration.Data.ReadOnly then
      data.Configuration.Data <- ctx.Message
      data.Configuration.Save()
      if data.Configuration.Data.Verbose then
        printfn "Configuration updated"
    elif data.Configuration.Data.Verbose then
      printfn "Configuration update refused"
    ctx.Result <- Suave.Successful.OK ""

  let getConfig (ctx:MsgRequestEventArgs<_>) =
    ctx.Result <- this.BuildJsonReply data.Configuration.Data

  do
    stopwatch.Start()
    this.Root <- Suave.Redirection.moved_permanently "index.html"
    this.Browsable <- true

    +(!!this.RegisterHttpEvent("tunnel") |-> sendData)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterReplyEvent("stats") |-> stats)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterReplyEvent("getConfig") |-> getConfig)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterReplyEvent("saveConfig") |-> updateConfig)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterEvent("reloadConfig") |-> fun _ -> data.Configuration.Load())
    |> this.ActivateNet
    |> ignore

  static member DefaultConfig = {
    DestinationHost = "192.0.2.3" // example destination
    DestinationPort = 514
    ReadOnly = false
    Verbose = false
  }
