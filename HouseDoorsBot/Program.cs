using Refit;

using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

const string SETTINGS_FILENAME = "Settings.json";

var settingsFilePath = Path.Combine(Environment.CurrentDirectory, SETTINGS_FILENAME);

var Settings = LoadSettings(settingsFilePath);

var UserCommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "0", Commands.GetChatId },
	{ "1", Commands.OpenEntranceDoor },
	{ "2", Commands.OpenMainDoor },
	{ "3", Commands.OpenNearShopDoor },
	{ "4", Commands.OpenNearParkingDoor}
});

var AdminCommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "111", Commands.GenerateCode},
	{ "000", Commands.DeleteCode}
});

var CommandDoorDictionary = new ReadOnlyDictionary<Commands, Doors>(new Dictionary<Commands, Doors>
{
	{ Commands.OpenEntranceDoor, Doors.Entrance },
	{ Commands.OpenMainDoor, Doors.Main },
	{ Commands.OpenNearShopDoor, Doors.NearShop },
	{ Commands.OpenNearParkingDoor, Doors.NearParking },
});

var botClient = new TelegramBotClient(Settings.HouseBotAccessToken);

ReceiverOptions receiverOptions = new()
{
	AllowedUpdates = Array.Empty<UpdateType>()
};

using CancellationTokenSource cts = new();

botClient.StartReceiving(
	updateHandler: HandleUpdateAsync,
	pollingErrorHandler: HandlePollingErrorAsync,
	receiverOptions: receiverOptions,
	cancellationToken: cts.Token
);

var me = await botClient.GetMeAsync();

Console.WriteLine($"Start listening for @{me.Username}");
Console.ReadLine();

cts.Cancel();

static Settings LoadSettings(string filePath)
{
	if (!filePath.IsExistFile())
		throw new FileNotFoundException("Settings file is not found.");

	var fileText = System.IO.File.ReadAllText(filePath);
	var settings = JsonSerializer.Deserialize<Settings>(fileText);

	return settings ?? new();
}

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
	if (update.Message is not { } message)
		return;
	if (message.Text is not { } messageText)
		return;

	var chatId = message.Chat.Id;
	var user = $"{message.Chat.FirstName} {message.Chat.LastName} (@{message.Chat.Username})";

	Console.WriteLine($"Received a '{messageText}' message from {user}, chat {chatId}.");

	var result = await ExecuteCommandAsync(chatId, messageText);

	Console.WriteLine($"Answer: '{result}'. Message to {user}, chat {chatId}.");

	var sentMessage = await botClient.SendTextMessageAsync(
		chatId: chatId,
		result,
		replyToMessageId: update.Message.MessageId,
		allowSendingWithoutReply: true,
		cancellationToken: cancellationToken);
}

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
	var ErrorMessage = exception switch
	{
		ApiRequestException apiRequestException
			=> $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
		_ => exception.ToString()
	};

	Console.WriteLine(ErrorMessage);

	return Task.CompletedTask;
}

async Task<string> ExecuteCommandAsync(long chatId, string messageText)
{
	const int FlatId = 451352;
	const string commandNotExistMessage = "There is no such command";

	if (messageText.Equals("/start"))
		return $"Hello:) Your ChatId is {chatId}";

	var isAdmin = IsAdmin(chatId);
	var isUser = IsUser(chatId);

	if (!isAdmin && !isUser)
		return "No access";

	if (messageText.Equals("/help"))
		return GetAvailableCommands(chatId);

	var isExistAdminCommand = AdminCommandsDictionary.TryGetValue(messageText, out var adminCommand);
	var isExistUserCommand = UserCommandsDictionary.TryGetValue(messageText, out var userCommand);

	if (isAdmin && isExistAdminCommand)
	{
		if (adminCommand is Commands.GenerateCode)
			return await GenerateCodeCommandAsync(FlatId);

		if (adminCommand is Commands.DeleteCode)
			return await DeleteCodeCommandAsync(FlatId);

		return commandNotExistMessage;
	}
	else if (isExistUserCommand)
	{
		var isOpenDoorCommand = CommandDoorDictionary.TryGetValue(userCommand, out var door);

		if (isOpenDoorCommand)
			return await OpenDoorCommandAsync(door);

		if (userCommand is Commands.GetChatId)
			return $"Your ChatId is {chatId}";

		return commandNotExistMessage;
	}
	else
		return commandNotExistMessage;
}

bool IsAdmin(long chatId) => Settings?.HouseBotAdmins?.Contains(chatId) ?? false;

bool IsUser(long chatId) => Settings.HouseBotUsers?.Contains(chatId) ?? false;

string GetAvailableCommands(long chatId)
	=> IsAdmin(chatId) ? GetAvailableAdminCommands()
		: IsUser(chatId) ? GetAvailableUserCommands()
		: "No Available Commands";

string GetAvailableAdminCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach (var command in UserCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	foreach (var command in AdminCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	sb.AppendLine("Or write something to chat with ChatGPT");

	return sb.ToString();
}

string GetAvailableUserCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach (var command in UserCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	sb.AppendLine("Or write something to chat with ChatGPT");

	return sb.ToString();
}

async Task<string> GenerateCodeCommandAsync(int flatId)
{
	var api = RestService.For<IApi>(Settings.HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.GenerateCodeAsync(Settings.HouseAuthToken, requestId,
		new
		{
			devices_ids = Array.ConvertAll((Doors[])Enum.GetValues(typeof(Doors)), value => (int)value),
			flat_id = flatId
		})
		.HandleExceptionAndGetStringResult(it =>
		{
			if (!it.IsSuccessStatusCode)
				return $"Error [{it.StatusCode.ToString()}]";

			using var jsonDocument = JsonDocument.Parse(it.Content);

			var isExistData = jsonDocument.RootElement.TryGetProperty("data", out var dataElement);
			var isExistCode = dataElement.TryGetProperty("code", out var codeElement);
			var isExistExpiresAt = dataElement.TryGetProperty("expires_at", out var expiresAtElement);
			var sb = new StringBuilder();

			sb.AppendLine($"Code: {codeElement.GetString()}");
			sb.AppendLine($"Expires at: {DateTime.Parse(expiresAtElement.GetString()).ToString("g")}");

			return sb.ToString();
		});
}

async Task<string> DeleteCodeCommandAsync(int flatId)
{
	var api = RestService.For<IApi>(Settings.HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.DeleteCodeAsync(Settings.HouseAuthToken, requestId, flatId)
			.HandleExceptionAndGetStringResult(it => it.StatusCode is HttpStatusCode.NoContent
				? $"Last code is deleted"
				: $"Error [{it.StatusCode.ToString()}]");
}

async Task<string> OpenDoorCommandAsync(Doors door)
{
	var api = RestService.For<IApi>(Settings.HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.OpenDoorAsync(Settings.HouseAuthToken, requestId, (short)door)
			.HandleExceptionAndGetStringResult(it => it.IsSuccessStatusCode
				? $"Door ({door}) is opened"
				: $"Error [{it.StatusCode.ToString()}]");
}

string GenerateRequestId() => Guid.NewGuid().ToString().ToUpperInvariant();

enum Commands : byte
{
	GetChatId,
	OpenEntranceDoor,
	OpenMainDoor,
	OpenNearShopDoor,
	OpenNearParkingDoor,
	GenerateCode,
	DeleteCode
}

enum Doors : short
{
	Entrance = 6700,
	Main = 6701,
	NearShop = 13519,
	NearParking = 14149
}

[Headers("User-Agent: :)")]
interface IApi
{
	[Post("/v2/app/devices/{doorId}/open")]
	Task<IApiResponse<string>> OpenDoorAsync(
		[Authorize("Bearer")] string token,
		[Header("X-Request-Id")] string requestId,
		short doorId);

	[Post("/v3/app/codes/generate")]
	Task<IApiResponse<string>> GenerateCodeAsync(
		[Authorize("Bearer")] string token,
		[Header("X-Request-Id")] string requestId,
		[Body] object body);

	[Delete("/v2/app/flats/{flatId}/intercode")]
	Task<IApiResponse<string>> DeleteCodeAsync(
		[Authorize("Bearer")] string token,
		[Header("X-Request-Id")] string requestId,
		int flatId);
}

static class TaskExtention
{
	public static async Task<string> HandleExceptionAndGetStringResult(this Task<IApiResponse<string>> task, Func<IApiResponse<string>, string> func)
	{
		try
		{
			var result = await task;

			return func(result);
		}
		catch (ApiException apiException)
		{
			return $"Exception ({apiException.Message})";
		}
	}
}

static class ArgumentExceptionExtention
{
	public static string ThrowIfNullOrWhiteSpace(this string argument, string argumentName, string message)
	{
		if (string.IsNullOrWhiteSpace(argument))
			ThrowArgumentNullException(argumentName, message);

		return argument;
	}

	private static void ThrowArgumentNullException(string argumentName, string message)
		=> throw new ArgumentNullException(argumentName, message);
}

static class GlobalExtentions
{
	public static bool IsExistFile(this string filePath)
		=> !string.IsNullOrWhiteSpace(filePath) && System.IO.File.Exists(filePath);
}

class Settings
{
	public string HouseApiUrl { get; set; } = string.Empty;

	public string HouseAuthToken { get; set; } = string.Empty;

	public string HouseBotAccessToken { get; set; } = string.Empty;

	public long[] HouseBotAdmins { get; set; } = Array.Empty<long>();

	public long[] HouseBotUsers { get; set; } = Array.Empty<long>();
}