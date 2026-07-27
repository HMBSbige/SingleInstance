namespace SingleInstance.TestApp;

public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args is not ["server", _])
		{
			await Console.Error.WriteLineAsync("Usage: SingleInstance.TestApp server <identifier>");
			return 1;
		}

		using SingleInstanceService service = new(args[1]);

		if (!service.IsFirstInstance)
		{
			await Console.Error.WriteLineAsync("The helper could not become the first instance.");
			return 2;
		}

		service.StartListening(command => Console.WriteLine($"COMMAND:{command}"));
		Console.WriteLine("READY");
		_ = await Console.In.ReadLineAsync();
		return 0;
	}
}
