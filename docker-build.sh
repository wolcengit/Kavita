#! /bin/bash
set -e

# 用法:
#   ./docker-build.sh [选项] [RID...]
#
# 示例:
#   ./docker-build.sh --image registry.cn-hangzhou.aliyuncs.com/wolcen/kavita --tag 20260906 linux-x64
#   ./docker-build.sh --image=wolcen/kavita --tag=v0.8.0
#   KAVITA_IMAGE=wolcen/kavita ./docker-build.sh linux-arm64    # 环境变量仍支持
#
# 镜像名/tag 的优先级: 命令行参数 > 环境变量 > 内置默认值

outputFolder='_output'

usage()
{
    cat <<'EOF'
用法: ./docker-build.sh [选项] [RID...]

选项:
  --image <name>     镜像名称  默认: 环境变量 KAVITA_IMAGE, 否则 kizaing/kavita
  --tag <tag>        镜像 tag  默认: 环境变量 KAVITA_TAG,   否则 nightly
  -h, --help         显示本帮助

RID (可多传, 空格分隔; 不传则构建全部平台):
  linux-x64          构建 linux/amd64
  linux-arm          构建 linux/arm/v7
  linux-arm64        构建 linux/arm64

示例:
  ./docker-build.sh --image registry.cn-hangzhou.aliyuncs.com/wolcen/kavita --tag 20260906 linux-x64
  ./docker-build.sh --image=wolcen/kavita --tag=v0.8.0 linux-x64 linux-arm64
  ./docker-build.sh linux-arm64
EOF
}

# 环境变量作为基线, 稍后由命令行参数覆盖
KAVITA_IMAGE="${KAVITA_IMAGE:-kizaing/kavita}"
KAVITA_TAG="${KAVITA_TAG:-nightly}"

TARGET_PLATFORMS=()

while [ "$#" -gt 0 ]; do
    case "$1" in
        --image)
            [ "$#" -ge 2 ] || { echo "错误: --image 缺少参数值"; exit 1; }
            KAVITA_IMAGE="$2"
            shift 2
            ;;
        --image=*)
            KAVITA_IMAGE="${1#*=}"
            shift
            ;;
        --tag)
            [ "$#" -ge 2 ] || { echo "错误: --tag 缺少参数值"; exit 1; }
            KAVITA_TAG="$2"
            shift 2
            ;;
        --tag=*)
            KAVITA_TAG="${1#*=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        -*)
            echo "错误: 未知选项 '$1'"
            echo
            usage
            exit 1
            ;;
        *)
            TARGET_PLATFORMS+=("$1")
            shift
            ;;
    esac
done

# 校验命令行传入的空值
[ -n "$KAVITA_IMAGE" ] || { echo "错误: 镜像名称不能为空"; exit 1; }
[ -n "$KAVITA_TAG" ]   || { echo "错误: 镜像 tag 不能为空"; exit 1; }

if [ "${#TARGET_PLATFORMS[@]}" -eq 0 ]; then
    TARGET_PLATFORMS=("linux-x64" "linux-arm" "linux-arm64")
fi

# RID -> Docker platform 映射
rid_to_docker_platform()
{
    case "$1" in
        linux-x64)   echo "linux/amd64" ;;
        linux-arm)   echo "linux/arm/v7" ;;
        linux-arm64) echo "linux/arm64" ;;
        *)           echo "unsupported-rid:$1" ;;
    esac
}

# 提前校验所有 RID: 避免 BuildUI 跑完(数分钟)后才报错
for runtime in "${TARGET_PLATFORMS[@]}"
do
    case "$(rid_to_docker_platform "$runtime")" in
        unsupported-rid:*)
            echo "错误: 不支持的 RID '$runtime'"
            echo "Supported: linux-x64, linux-arm, linux-arm64"
            exit 1
            ;;
    esac
done

ProgressStart()
{
    echo "Start '$1'"
}

ProgressEnd()
{
    echo "Finish '$1'"
}

Build()
{
	local RID="$1"

    ProgressStart "Build for $RID"

    slnFile=Kavita.sln

    dotnet clean $slnFile -c Release

	  dotnet msbuild -restore $slnFile -p:Configuration=Release -p:Platform="Any CPU" -p:RuntimeIdentifiers=$RID

    ProgressEnd "Build for $RID"
}

BuildUI()
{
    ProgressStart 'Building UI'
    echo 'Removing old wwwroot'
    rm -rf Kavita.Server/wwwroot/*
    cd UI/Web/ || exit
    echo 'Installing web dependencies'
    # --include=dev: 必须安装 devDependencies（@angular/cli 在其中）。
    # 否则环境中若存在 NODE_ENV=production, npm 会自动 omit=dev, 导致 ng: not found。
    npm ci --legacy-peer-deps --include=dev
    echo 'Building UI'
    npm run prod
    echo 'Copying back to Kavita wwwroot'
    mkdir -p ../../Kavita.Server/wwwroot
    cp -R dist/browser/* ../../Kavita.Server/wwwroot
    cd ../../ || exit
    ProgressEnd 'Building UI'
}

Package()
{
    local runtime="$1"
    local lOutputFolder=../_output/"$runtime"/Kavita

    ProgressStart "Creating $runtime Package"

    # TODO: Use no-restore? Because Build should have already done it for us
    echo "Building"
    cd Kavita.Server
    echo dotnet publish -c Release --no-restore --self-contained --runtime $runtime -o "$lOutputFolder"
    dotnet publish -c Release --no-restore --self-contained --runtime $runtime -o "$lOutputFolder"

    echo "Copying Install information"
    cp ../INSTALL.txt "$lOutputFolder"/README.txt

    echo "Copying LICENSE"
    cp ../LICENSE "$lOutputFolder"/LICENSE.txt

    echo "Renaming Kavita.Server -> Kavita"
    mv "$lOutputFolder"/Kavita.Server "$lOutputFolder"/Kavita

    echo "Creating tar"
    cd ../$outputFolder/"$runtime"/
    tar -czvf ../kavita-$runtime.tar.gz Kavita

    ProgressEnd "Creating $runtime Package"

}

dir=$PWD

if [ -d _output ]
then
	rm -r _output/
fi

BuildUI

# RID 已在参数解析阶段校验, 此处只负责构建并收集 Docker 平台
DOCKER_PLATFORMS=()
for runtime in "${TARGET_PLATFORMS[@]}"
do
    platform=$(rid_to_docker_platform "$runtime")

    Build "$runtime"
    Package "$runtime"
    cd "$dir"

    DOCKER_PLATFORMS+=("$platform")
done

# 拼接为逗号分隔的 platform 列表
PLATFORM_LIST=$(IFS=, ; echo "${DOCKER_PLATFORMS[*]}")

echo "Image:     ${KAVITA_IMAGE}:${KAVITA_TAG}"
echo "Platforms: $PLATFORM_LIST"

#Builds Docker images (image name & tag from KAVITA_IMAGE / KAVITA_TAG)
# --provenance=false / --sbom=false:
#   BuildKit 默认会附带 provenance、SBOM 证明清单（application/vnd.oci.empty.v1+json 等）。
#   Docker Hub 接受, 但阿里云 ACR 会拒绝:
#     denied: unknown manifest class for application/vnd.oci.empty.v1+json
#   若目标仓库支持 OCI 证明清单, 可去掉这两个参数。
docker buildx build -t "${KAVITA_IMAGE}:${KAVITA_TAG}" --platform "$PLATFORM_LIST" --provenance=false --sbom=false . --push
