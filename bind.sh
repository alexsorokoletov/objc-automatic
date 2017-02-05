#!/bin/bash
mono .paket/paket.bootstrapper.exe
exit_code=$?
if [ $exit_code -ne 0 ]; then
exit $exit_code
fi

mono .paket/paket.exe install
exit_code=$?
if [ $exit_code -ne 0 ]; then
exit $exit_code
fi

[ ! -e build.fsx ] && mono .paket/paket.exe update
mono --runtime=v4.0 packages/Auto/FAKE/tools/FAKE.exe ./build.fsx $@