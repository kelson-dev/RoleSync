FROM mcr.microsoft.com/dotnet/runtime-deps:6.0.0-rc.1-alpine3.14
# source.tar.gz is supplied by the Release webhook listener
RUN mkdir /application/
ADD bin/Release/net6.0/linux-musl-x64/publish /application/
WORKDIR /application/
ENTRYPOINT [ "/application/Kelson.RoleSyncBot" ]