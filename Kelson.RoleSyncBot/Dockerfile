FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-alpine
RUN apk add icu-libs && apk add libstdc++ && apk add libgcc
RUN mkdir /application/
ADD bin/Release/net6.0/linux-musl-x64/publish /application/
WORKDIR /application/
ENTRYPOINT [ "/application/Kelson.RoleSyncBot" ]