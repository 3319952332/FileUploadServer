# 第一阶段：构建
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制项目文件
COPY FileUploadServer.slnx ./
COPY FileUploadServer.Core/FileUploadServer.Core.csproj FileUploadServer.Core/
COPY FileUploadServer.Infrastructure/FileUploadServer.Infrastructure.csproj FileUploadServer.Infrastructure/
COPY FileUploadServer.Web/FileUploadServer.Web.csproj FileUploadServer.Web/

# 还原包
RUN dotnet restore

# 复制所有源码
COPY . .

# 发布
RUN dotnet publish FileUploadServer.Web/FileUploadServer.Web.csproj -c Release -o /app/publish

# 第二阶段：运行
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# 暴露端口
EXPOSE 5005

# 设置时区为北京时间
ENV TZ=Asia/Shanghai
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

# 启动应用
ENTRYPOINT ["dotnet", "FileUploadServer.Web.dll"]
