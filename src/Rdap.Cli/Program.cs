using Rdap.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var app = new CliApp(Console.Out, Console.Error)
{
    UseColor = Ansi.ShouldUseColor(),
};

return await app.RunAsync(args);
