# dotnet-cross

[![NuGet version (simpleserver-tool)](https://img.shields.io/nuget/v/dotnet-cross?color=blue)](https://www.nuget.org/packages/dotnet-cross/)
[![Build app](https://github.com/giggio/dotnet-cross/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/giggio/dotnet-cross/actions/workflows/build.yml)

A tool to cross compile .NET apps to the correct architecture and C library.

## Running

Run it like this:

```bash
dotnet cross <dotnet args>
```

For example:

```bash
dotnet cross build
dotnet cross build --runtime linux-musl-x64 -c Release
```

If you add `--runtime` or `-r` it will create a container image and use that to build.

If you've seen Rust's Cross, it's the same idea.

## Installing dotnet cli tool

This tool has to be installed as a dotnet global tool, will you need to have the .NET Sdk installed.

```bash
dotnet tool install --global dotnet-cross
```

## Contributing

Questions, comments, bug reports, and pull requests are all welcome.  Submit them at
[the project on GitHub](https://github.com/giggio/dotnet-cross).

Bug reports that include steps-to-reproduce (including code) are the
best. Even better, make them in the form of pull requests.

## Author

[Giovanni Bassi](https://github.com/giggio).

## License

Licensed under the MIT License.
