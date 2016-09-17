namespace IoX.Module.UdpTunnel

open EvReact.Expr
open IoX.Modules
open Suave
open Suave.EvReact
open System
open System.IO
open System.Net
open System.Text

open Utils

type CollectorConfiguration = {
  Destination: Uri
  Encoding: string
  StoreRegex: string
  UrgentRegex: string
  SpamRegex: string
  Port: int
  BufferSizeThreshold: int64
  BufferTimeoutMS: int
  ReadOnly: bool
  Verbose: bool
}

type CollectorStats = {
  ElapsedMS: int64
  Total: InOutStats
  Store: InOutStats
  Urgent: InOutStats
  Normal: InOutStats
  Spam: InOutStats
}

type MessagePriority = Urgent | Normal | Spam

[<Module(
  Name = "UDP datagrams collector",
  Description = "IoX module that collects, compresses and forwards UDP datagrams to an IoX UDP dispatcher."
)>]
type CollectorModule(data: IModuleData<CollectorConfiguration>) as this =
  inherit DriverModule()

  // Stats
  let stopwatch = Diagnostics.Stopwatch()
  let mutable  totalIncoming = noPackets
  let mutable  totalOutgoing = noPackets
  let mutable  storeIncoming = noPackets
  let mutable  storeOutgoing = noPackets
  let mutable urgentIncoming = noPackets
  let mutable urgentOutgoing = noPackets
  let mutable normalIncoming = noPackets
  let mutable normalOutgoing = noPackets
  let mutable   spamIncoming = noPackets

  let stats (ctx: MsgRequestEventArgs<_>) =
    ctx.Result <- this.BuildJsonReply {
      ElapsedMS = stopwatch.ElapsedMilliseconds
      Total = id {
        Incoming = totalIncoming
        Outgoing = totalOutgoing
      }
      Store = id {
        Incoming = storeIncoming
        Outgoing = storeOutgoing
      }
      Urgent = id {
        Incoming = urgentIncoming
        Outgoing = urgentOutgoing
      }
      Normal = id {
        Incoming = normalIncoming
        Outgoing = normalOutgoing
      }
      Spam = id {
        Incoming = spamIncoming
        Outgoing = noPackets
      }
    }

  // Coordination
  let orch = EvReact.Orchestrator.create()
  let atomicAction f =
    let evt = EvReact.Event()
    EvReact.Utils.start0 orch +(!!evt.Publish |-> f) |> ignore
    fun _ -> evt.Trigger(Unchecked.defaultof<_>)

  // Storage management
  let mutable storage : Compressor = null
  let renewStorage _ =
    if not(isNull storage) then
      storage.Dispose()
    // Some filesystems prohibit ':' in filenames
    let timeStamp = DateTime.UtcNow.ToString("o").Replace(":", "")
    let folder = Path.Combine(data.Path, "log")
    let fileName = Path.Combine(folder, sprintf "udp-tunnel.%s.dump" timeStamp)
    Directory.CreateDirectory(folder) |> ignore
    storage <- new Compressor(File.Create(fileName))
    if data.Configuration.Data.Verbose then
      printfn "Storing messages to %s" fileName
  let renewStorage = atomicAction renewStorage
  let storageTimer = new Threading.Timer(Threading.TimerCallback(renewStorage))

  let storeMessage (_,msg) =
    let origin = storage.TotalOut
    storage.SyncWriteMessage(msg)
    let compressedLength = storage.TotalOut - origin
    addPackets &storeOutgoing 1L compressedLength
    if data.Configuration.Data.Verbose then
      printfn "Stored message of %A bytes compressed to %A" msg.LongLength compressedLength

  // Message tunneling
  let sendMessages (compressed:_[]) =
    async {
      try
        use client = new System.Net.WebClient()
        if data.Configuration.Data.Verbose then
          printfn "Sending %A bytes of compressed data" compressed.LongLength
        addPackets &totalOutgoing 1L compressed.LongLength
        client.UploadData(data.Configuration.Data.Destination, compressed) |> ignore
      with e -> printfn "Could not send message: %A" e
    } |> Async.Start

  let forwardMessage (_,msg) =
    let compressed = compressedMsg msg
    sendMessages compressed
    addPackets &urgentOutgoing 1L compressed.LongLength
    if data.Configuration.Data.Verbose then
      printfn "Forwarded message of %A bytes" msg.LongLength

  // Buffer management
  let mutable msgsInBuffer = 0L
  let mutable compressedBuffer = new MemoryStream()
  let mutable buffer = new Compressor(compressedBuffer)

  let flushBuffer _ =
    if data.Configuration.Data.Verbose then
      printfn "Flushing %A buffered messages" msgsInBuffer
    buffer.Dispose()
    let compressed = compressedBuffer.ToArray()
    sendMessages compressed
    addPackets &normalOutgoing msgsInBuffer compressed.LongLength
    compressedBuffer <- new MemoryStream()
    buffer <- new Compressor(compressedBuffer)
    msgsInBuffer <- 0L
  let flushBuffer = atomicAction flushBuffer

  let timeoutFlush _ =
    if data.Configuration.Data.Verbose then
      printfn "Buffer timer triggered"
    flushBuffer()
  let flushTimer = new Threading.Timer(Threading.TimerCallback(timeoutFlush))

  let bufferMessage (_,msg) =
    buffer.BufferedWriteMessage(msg)
    msgsInBuffer <- msgsInBuffer + 1L
    if data.Configuration.Data.Verbose then
      printfn "Buffered message of %A bytes" msg.LongLength
    if compressedBuffer.Length >= data.Configuration.Data.BufferSizeThreshold then
      if data.Configuration.Data.Verbose then
        printfn "Buffering threshold exceeded"
      flushBuffer()
    elif msgsInBuffer = 1L then
      flushTimer.Change(
        dueTime = data.Configuration.Data.BufferTimeoutMS,
        period  = Threading.Timeout.Infinite)
      |> ignore
      if data.Configuration.Data.Verbose then
        printfn "Started flush timer"

  // Socket management
  let newMessage = EvReact.Event.create "newMessage"
  let onNewMessage = newMessage.Publish

  let alwaysNull = fun _ -> null
  let encode = ref alwaysNull

  let mutable socket = null
  let mutable needSocketRefresh = true

  let rec beginReceive() =
    if needSocketRefresh then
      needSocketRefresh <- false
      if not(isNull socket) then
        (socket :> IDisposable).Dispose()
      socket <- new Sockets.UdpClient(data.Configuration.Data.Port)
      if data.Configuration.Data.Verbose then
        printfn "Waiting for datagrams on port %A" data.Configuration.Data.Port

    socket.BeginReceive(AsyncCallback cb, socket) |> ignore
  and cb result =
    try
      let mutable endPoint = IPEndPoint(IPAddress.Any, 0)
      let recvSocket = result.AsyncState :?> Sockets.UdpClient
      let msg = recvSocket.EndReceive(result, &endPoint)
      if needSocketRefresh then
        // The socket is about to change, hence the data received on the current
        // port is going to be ignored in any case.
        ()
      else
        if data.Configuration.Data.Verbose then
          printfn "Received message of %A bytes: %s" msg.LongLength (!encode msg)
        async { newMessage.Trigger((!encode msg, msg)) } |> Async.Start
    with e -> printfn "Unexpected error: %A" e
    beginReceive()

  // Configuration management
  let alwaysNormal = fun _ -> Normal
  let alwaysTrue = fun _ -> true

  let shouldStore = ref alwaysTrue
  let classify = ref alwaysNormal

  let buildClassifier cfg =
    try
      let encoding = Encoding.GetEncoding(cfg.Encoding)
      let storeR = RegularExpressions.Regex(cfg.StoreRegex, RegularExpressions.RegexOptions.Compiled)
      let urgentR = RegularExpressions.Regex(cfg.UrgentRegex, RegularExpressions.RegexOptions.Compiled)
      let spamR = RegularExpressions.Regex(cfg.SpamRegex, RegularExpressions.RegexOptions.Compiled)
      encode := fun msg -> try encoding.GetString(msg) with _ -> null
      shouldStore := fun s -> isNull s || storeR.IsMatch(s)
      classify := fun s ->
        if isNull s then Normal
        elif urgentR.IsMatch(s) then Urgent
        elif spamR.IsMatch(s) then Spam
        else Normal
    with e ->
      if data.Configuration.Data.Verbose then
        printfn "Could not build the classifier for %A %A %A: %A" cfg.Encoding cfg.UrgentRegex cfg.StoreRegex e
      shouldStore := alwaysTrue
      classify := alwaysNormal

  let updateConfig cfg =
    data.Configuration.Data <- cfg
    buildClassifier cfg

  let saveConfig (ctx:MsgRequestEventArgs<_>) =
    if not data.Configuration.Data.ReadOnly then
      let oldPort = data.Configuration.Data.Port
      updateConfig ctx.Message
      data.Configuration.Save()
      if data.Configuration.Data.Verbose then
        printfn "Configuration updated"
      if oldPort <> data.Configuration.Data.Port then
        // Send an empty message to get to the callback
        needSocketRefresh <- true
        socket.SendAsync([| |], 0, IPEndPoint(IPAddress.Loopback, oldPort)) |> ignore
    elif data.Configuration.Data.Verbose then
      printfn "Configuration update refused"
    ctx.Result <- Suave.Successful.OK ""

  let getConfig (ctx: MsgRequestEventArgs<_>) =
    ctx.Result <- this.BuildJsonReply data.Configuration.Data

  let getEncodings (ctx: MsgRequestEventArgs<_>) =
    ctx.Result <-
      Encoding.GetEncodings()
      |> Array.map (fun enc -> enc.Name)
      |> this.BuildJsonReply

  // Message dispatching
  let demux = EvReact.Utils.Demultiplexer(onNewMessage, fun (s,_) -> !classify s)
  let alwaysExpr = +(!!onNewMessage)
                      |-> (fun (_,msg:_[]) -> addPackets &totalIncoming 1L msg.LongLength)

  let storeExpr  = +(onNewMessage %- (fun (s,_) -> !shouldStore s))
                      |-> (fun (_,msg:_[]) -> addPackets &storeIncoming 1L msg.LongLength)
                      |-> storeMessage

  let urgentExpr = +(!!demux.[Urgent])
                      |-> (fun (_,msg:_[]) -> addPackets &urgentIncoming 1L msg.LongLength)
                      |-> forwardMessage

  let normalExpr = +(!!demux.[Normal])
                      |-> (fun (_,msg:_[]) -> addPackets &normalIncoming 1L msg.LongLength)
                      |-> bufferMessage

  let spamExpr   = +(!!demux.[Spam])
                      |-> (fun (_,msg:_[]) -> addPackets &spamIncoming 1L msg.LongLength)

  do
    stopwatch.Start()
    this.Root <- Redirection.moved_permanently "index.html"
    this.Browsable <- true

    +(!!this.RegisterReplyEvent("stats") |-> stats)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterReplyEvent("getEncodings") |-> getEncodings)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterReplyEvent("getConfig") |-> getConfig)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterReplyEvent("saveConfig") |-> saveConfig)
    |> this.ActivateNet
    |> ignore

    +(!!this.RegisterEvent("reloadConfig") |-> (fun _ -> data.Configuration.Load()))
    |> this.ActivateNet
    |> ignore

    EvReact.Utils.start0 orch alwaysExpr |> ignore
    EvReact.Utils.start0 orch storeExpr  |> ignore
    EvReact.Utils.start0 orch urgentExpr |> ignore
    EvReact.Utils.start0 orch normalExpr |> ignore
    EvReact.Utils.start0 orch spamExpr   |> ignore

    let msInADay = 24 * 60 * 60 * 1000
    let currMS = int System.DateTime.Now.TimeOfDay.TotalMilliseconds
    storageTimer.Change(dueTime=msInADay-currMS, period=msInADay) |> ignore

    updateConfig data.Configuration.Data
    renewStorage()
    beginReceive()

  static member DefaultConfig = {
    Destination = Uri("http://192.0.2.2:8080/tunnel")
    Encoding = "UTF-8"
    StoreRegex = ".*(?# any string; always matched)"
    UrgentRegex = "$.(?# end of string followed by any character; never matched)"
    SpamRegex = "$.(?# end of string followed by any character; never matched)"
    Port = 514
    BufferSizeThreshold = 65536L
    BufferTimeoutMS = 15000
    ReadOnly = false
    Verbose = false
  }
