# dotnet-cross

A tool to cross compile .NET apps to the correct architecture and C library.

## Running

Download the version to your OS (see bellow) and run it like this:

```bash
dotnet cross <dotnet args>
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
