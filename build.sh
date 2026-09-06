#! /bin/bash
set -e

outputFolder='_output'

CheckRequirements()
{
    if ! command -v npm &> /dev/null
    then
        echo "Warning!!! npm not found, it is required for building Kavita!"
    fi
    if ! command -v dotnet &> /dev/null
    then
        echo "Warning!!! dotnet not found, it is required for building Kavita!"
    fi
}

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
    ProgressStart 'Build'

    rm -rf $outputFolder

    slnFile=Kavita.sln

    dotnet clean $slnFile -c Release

    if [[ -z "$RID" ]];
    then
        dotnet msbuild -restore $slnFile -p:Configuration=Release -p:Platform="Any CPU"
    else
        dotnet msbuild -restore $slnFile -p:Configuration=Release -p:Platform="Any CPU" -p:RuntimeIdentifiers=$RID
    fi

    ProgressEnd 'Build'
}

BuildUI()
{
    ProgressStart 'Building UI'
    echo 'Removing old wwwroot'
    rm -rf Kavita.Server/wwwroot/*
    cd UI/Web/ || exit
    echo 'Installing web dependencies'
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
    echo dotnet publish -c Release --self-contained --runtime $runtime -o "$lOutputFolder"
    dotnet publish -c Release --self-contained --runtime $runtime -o "$lOutputFolder"

    echo "Recopying wwwroot due to bug"
    cp -R ./wwwroot/* $lOutputFolder/wwwroot

    echo "Removing EF Core design-time folders"
    rm -rf "$lOutputFolder"/BuildHost-net472
    rm -rf "$lOutputFolder"/BuildHost-netcore

    echo "Removing cache-long from config"
    rm -rf "$lOutputFolder"/config/cache-long

    echo "Copying Install information"
    cp ../INSTALL.txt "$lOutputFolder"/README.txt

    echo "Copying LICENSE"
    cp ../LICENSE "$lOutputFolder"/LICENSE.txt

    echo "Renaming Kavita.Server -> Kavita"
    if [ $runtime == "win-x64" ] || [ $runtime == "win-x86" ]
    then
        mv "$lOutputFolder"/Kavita.Server.exe "$lOutputFolder"/Kavita.exe
    else
        mv "$lOutputFolder"/Kavita.Server "$lOutputFolder"/Kavita
    fi

    mkdir -p $lOutputFolder/config
    echo "Copying appsettings.json"
    cp config/appsettings.json $lOutputFolder/config/appsettings-init.json

    echo "Creating tar"
    cd ../$outputFolder/"$runtime"/
    tar -czvf ../kavita-$runtime.tar.gz Kavita


    ProgressEnd "Creating $runtime Package"
}

# 发布 Kavita.Importer: self-contained 单文件(参考 publish-release.sh 的参数约定)
PackageImporter()
{
    local runtime="$1"
    local lOutputFolder=../_output/importer-"$runtime"
    local exeSuffix=""
    if [[ "$runtime" == win-* ]]; then
        exeSuffix=".exe"
    fi

    ProgressStart "Creating $runtime Importer Package (single-file)"

    cd Kavita.Importer || exit

    echo "Publishing Kavita.Importer ($runtime, single-file)"
    dotnet publish Kavita.Importer.csproj -c Release --self-contained -r "$runtime" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none -p:DebugSymbols=false \
        -o "$lOutputFolder"

    echo "Copying LICENSE"
    cp ../LICENSE "$lOutputFolder"/LICENSE

    # SQLitePCLRaw.lib.e_sqlite3 在 linux/macos 的 runtimes/<rid>/native 下同时提供
    # libe_sqlite3.so(动态库) 与 libe_sqlite3.a(静态库)。self-contained 发布会把该目录
    # 摊平复制到输出根，而单文件打包器只识别可加载的 native library(.so)，.a 既不会被
    # 打进 bundle 也不会被清理，因此残留。运行时完全不需要，直接删除。
    echo "Removing static native libs not needed at runtime"
    rm -f "$lOutputFolder"/libe_sqlite3.a

    echo "Single-file Kavita.Importer for $runtime:"
    ls -la "$lOutputFolder"/Kavita.Importer*

    cd .. || exit

    ProgressEnd "Creating $runtime Importer Package"
}

# build.sh 支持的全部 Server RID(与 Importer 单文件输出一致)
TARGET_PLATFORMS=(win-x64 win-x86 linux-x64 linux-arm linux-arm64 linux-musl-x64 osx-x64 osx-arm64)

RID="$1"

CheckRequirements
BuildUI
Build

dir=$PWD

if [[ -z "$RID" ]];
then
    for runtime in "${TARGET_PLATFORMS[@]}";
    do
        Package "$runtime"
        cd "$dir"
        PackageImporter "$runtime"
        cd "$dir"
    done
else
    Package "$RID"
    cd "$dir"
    PackageImporter "$RID"
    cd "$dir"
fi
