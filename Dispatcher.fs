namespace IoX.Module.UdpTunnel

open EvReact.Expr
open IoX.Modules
open Suave.EvReact

open Utils

type DispatcherConfiguration = {
  Destinations: string
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

  let parseDests (s: string) =
    let parseEndPoint (x: string) =
      match x.Trim().Split(':') with
      | [| host; port |] -> (host, System.Int32.Parse port)
      | _ -> failwith "Malformed endpoint"
    s.Split(',') |> Array.map(parseEndPoint)

  let mutable dests = parseDests data.Configuration.Data.Destinations

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

  let sendMessage msg host port =
    socket.Send(msg, msg.Length, host, port) |> ignore
    addPackets &statsOutgoing 1L msg.LongLength
    if data.Configuration.Data.Verbose then
      printfn "Dispatched message of %A bytes to [%A:%A]: %s" msg.Length host port (asString msg)

  let sendData (ctx: HttpEventArgs) =
    ctx.Result <- Suave.Successful.OK ""
    let compressed = ctx.Context.request.rawForm
    if data.Configuration.Data.Verbose then
      printfn "Received %A bytes of compressed data" compressed.LongLength
    addPackets &statsIncoming 1L compressed.LongLength
    use compressedStream = new System.IO.MemoryStream(compressed)
    for msg in decompress compressedStream do
      for (host, port) in dests do
        sendMessage msg host port

  let updateConfig (ctx:MsgRequestEventArgs<_>) =
    if not data.Configuration.Data.ReadOnly then
      try
        dests <- parseDests data.Configuration.Data.Destinations
        data.Configuration.Data <- ctx.Message
        data.Configuration.Save()
        if data.Configuration.Data.Verbose then
          printfn "Configuration updated"
      with e -> printfn "%A" e
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
    Destinations = "192.0.2.3:514, 192.0.2.4:5140" // example destinations
    ReadOnly = false
    Verbose = false
  }
