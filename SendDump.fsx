module IoX.Module.UdpTunnel.Tools

open System.IO
open System.Net
open Utils

let usage () =
  printfn "Usage:"
  printfn "\tSendDump <host> <port>"
  printfn "Example:"
  printfn "\tSendDump 127.0.0.1 514"
  exit -1

[<EntryPoint>]
let main (args:string[]) =
  if args.Length <> 2 then usage()
  let hostname = args.[0]
  let port = try System.Int32.Parse(args.[1]) with _ -> usage()
  use socket = new Sockets.UdpClient()
  use stream = System.Console.OpenStandardInput()
  for msg in decompress stream do
    printfn "Message of %A bytes: %s" msg.Length (asString msg)
    socket.Send(msg, msg.Length, hostname, port) |> ignore
  0
