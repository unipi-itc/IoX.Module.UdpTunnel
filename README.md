## IoX UDP over HTTP tunnel

The project is a module for the [IoX](https://github.com/unipi-itc/IoX) infrastructure.

It implements a tunnel for UDP datagrams over an HTTP channels.

Some simple rules expressed as regular expressions control message forwarding:
 - urgent messages are forwarded immediately
 - spam messages are discarded
 - other messages are buffered and compressed before being forwarded.

Another regex controls which messages are stored locally in compressed dumps.

## Build and plug-in

To build the IoX.Module.UdpTunnel module you have to run:

    nuget restore
    msbuild IoX.Module.UdpTunnel.sln

You can use *xbuild* on Mono.

Note that you can also build from within Visual Studio but we are using wildcards inside `.fsproj` files that are
not supported by the F# project system yet. In this case you should copy the `static` folder in the output directory.

You can use the output of the project as a module in the IoX server.

### Build Status
|         |Linux|Windows|
|--------:|:---:|:-----:|
|**Status**|[![Build Status](https://travis-ci.org/unipi-itc/IoX.Module.UdpTunnel.svg?branch=master)](https://travis-ci.org/unipi-itc/IoX.Module.UdpTunnel)|[![Build status](https://ci.appveyor.com/api/projects/status/72jdee25qkgsgp47?branch=master&svg=true)](https://ci.appveyor.com/project/ranma42/iox-module-udptunnel)|
