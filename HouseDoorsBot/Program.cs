﻿using Refit;

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
const string LOGS_FILENAME = "Logs.txt";

var settingsFilePath = Path.Combine(Environment.CurrentDirectory, SETTINGS_FILENAME);
var logsFilePath = Path.Combine(Environment.CurrentDirectory, LOGS_FILENAME);

var Settings = LoadSettings(settingsFilePath);

var GuestCommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "0", Commands.RequestToUser }
});

var UserCommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "1", Commands.GetChatId },
	{ "2", Commands.OpenEntranceDoor },
	{ "3", Commands.OpenMainDoor },
	{ "4", Commands.OpenNearShopDoor },
	{ "5", Commands.OpenNearParkingDoor}
});

var AdminCommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "6", Commands.GetUsers},
	{ "7", Commands.GetRequestsToUsers},
	{ "8", Commands.GenerateCode},
	{ "9", Commands.DeleteCode}
});

var AdminParameterizedCommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "A", Commands.AcceptRequestToUsers},
	{ "D", Commands.DeleteFromUsers}
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
	const string IfNullOrWhiteSpaceMessage = "This is null or whitespace";

	if (!filePath.IsExistFile())
	{
		var newSettings = JsonSerializer.Serialize<Settings>(new());

		System.IO.File.WriteAllText(filePath, newSettings);
	}

	var fileText = System.IO.File.ReadAllText(filePath);
	var settings = JsonSerializer.Deserialize<Settings>(fileText);

	if (settings is not null)
		settings.FilePath = filePath;
	else
		throw new FileNotFoundException(filePath);

	settings.HouseApiUrl.ThrowIfNullOrWhiteSpace(nameof(settings.HouseApiUrl), $"{IfNullOrWhiteSpaceMessage} => {filePath}");
	settings.HouseAuthToken.ThrowIfNullOrWhiteSpace(nameof(settings.HouseApiUrl), $"{IfNullOrWhiteSpaceMessage} => {filePath}");
	settings.HouseBotAccessToken.ThrowIfNullOrWhiteSpace(nameof(settings.HouseApiUrl), $"{IfNullOrWhiteSpaceMessage} => {filePath}");

	return settings;
}

static void SaveSettings(string filePath, Settings settings)
{
	if (!filePath.IsExistFile())
		throw new FileNotFoundException("Settings file is not found");

	var updatedSettings = JsonSerializer.Serialize(settings);

	System.IO.File.WriteAllText(filePath, updatedSettings);
}

static string BuildLog(User user, string userMessage, string botResult)
{
	var sb = new StringBuilder();

	sb.AppendLine($"---------------------------------------------------------------");
	sb.AppendLine($"DateTime => {DateTime.Now}");
	sb.AppendLine($"User => {user}");
	sb.AppendLine($"Message => {userMessage}");
	sb.AppendLine($"Answer => {botResult}");
	sb.AppendLine($"---------------------------------------------------------------");
	sb.AppendLine();

	return sb.ToString();
}

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
	var logger = new Logger(logsFilePath);

	if (update.Message is not { } message)
		return;
	if (message.Text is not { } messageText)
		return;

	var user = new User
	{
		Id = message.Chat.Id,
		FirstName = message.Chat.FirstName,
		LastName = message.Chat.LastName,
		Username = message.Chat.Username
	};

	var result = await ExecuteCommandAsync(user, messageText, cancellationToken);
	var log = BuildLog(user, messageText, result);

	Console.WriteLine(log);

	var sentMessage = await botClient.SendTextMessageAsync(
		chatId: user.Id,
		result,
		replyToMessageId: update.Message.MessageId,
		allowSendingWithoutReply: true,
		cancellationToken: cancellationToken);

	logger.Log(log);
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

async Task<string> ExecuteCommandAsync(User user, string messageText, CancellationToken cancellationToken)
{
	const int FlatId = 451352;
	const string CommandNotExistMessage = "There is no such command";
	const string IncorrectCommandParameterMessage = "Incorrect command parameter";

	var isAdmin = IsAdmin(user);
	var isUser = IsUser(user);

	if (messageText.Equals("/help") || messageText.Equals("/start"))
		return GetAvailableCommands(user);

	var isExistAdminCommand = AdminCommandsDictionary.TryGetValue(messageText, out var adminCommand);
	var isExistUserCommand = UserCommandsDictionary.TryGetValue(messageText, out var userCommand);
	var isExistGuestCommand = GuestCommandsDictionary.TryGetValue(messageText, out var guestCommand);

	if (isAdmin && isExistAdminCommand)
	{
		if (adminCommand is Commands.GenerateCode)
			return await GenerateCodeCommandAsync(FlatId);

		if (adminCommand is Commands.DeleteCode)
			return await DeleteCodeCommandAsync(FlatId);

		if (adminCommand is Commands.GetUsers)
			return GetUsers();

		if (adminCommand is Commands.GetRequestsToUsers)
			return GetRequestsToUsers();

		return CommandNotExistMessage;
	}
	else if ((isAdmin || isUser) && isExistUserCommand)
	{
		var isOpenDoorCommand = CommandDoorDictionary.TryGetValue(userCommand, out var door);

		if (isOpenDoorCommand)
			return await OpenDoorCommandAsync(door);

		if (userCommand is Commands.GetChatId)
			return $"Your ChatId is {user.Id}";

		return CommandNotExistMessage;
	}
	else if (isExistGuestCommand)
	{
		if (guestCommand is Commands.RequestToUser)
			return AddRequestToUsers(user);

		return CommandNotExistMessage;
	}
	else if (isAdmin)
	{
		var message = messageText.Split(' ');

		if (message.Length < 2)
			return CommandNotExistMessage;

		var command = message[0];
		var parameter = message[1];
		var isExistAdminParameterizedCommand = AdminParameterizedCommandsDictionary.TryGetValue(command, out var adminParameterizedCommand);

		if (!isExistAdminParameterizedCommand)
			return CommandNotExistMessage;

		if (adminParameterizedCommand is Commands.AcceptRequestToUsers)
		{
			var isId = int.TryParse(parameter, out var id);

			return isId ? await AcceptRequestToUsers(id, cancellationToken) : IncorrectCommandParameterMessage;
		}

		if (adminParameterizedCommand is Commands.DeleteFromUsers)
		{
			var isId = int.TryParse(parameter, out var id);

			return isId ? await DeleteFromUsers(id, cancellationToken) : IncorrectCommandParameterMessage;
		}

		return CommandNotExistMessage;
	}
	else
		return CommandNotExistMessage;
}

bool IsAdmin(User user) => Settings.HouseBotAdmins.Contains(user);

bool IsUser(User user) => Settings.HouseBotUsers.Contains(user);

string GetAvailableCommands(User user)
	=> IsAdmin(user) ? GetAvailableAdminCommands()
		: IsUser(user) ? GetAvailableUserCommands()
		: GetAvailableGuestCommands();

string GetAvailableAdminCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach (var command in UserCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	foreach (var command in AdminCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	foreach (var command in AdminParameterizedCommandsDictionary)
		sb.AppendLine($"{command.Key} param - {command.Value}");

	return sb.ToString();
}

string GetAvailableUserCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach (var command in UserCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	return sb.ToString();
}

string GetAvailableGuestCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach (var command in GuestCommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	return sb.ToString();
}

string AddRequestToUsers(User user)
{
	if (Settings.HouseBotUsers.Contains(user))
		return "You are already a user";

	if (Settings.HouseBotRequestsToUsers.Contains(user))
		return "Request to user is already sended";

	Settings.HouseBotRequestsToUsers.Add(user);

	SaveSettings(Settings.FilePath, Settings);

	return "Request to user is sended";
}

async Task<string> OpenDoorCommandAsync(Doors door)
{
	var api = RestService.For<IApi>(Settings.HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.OpenDoorAsync(Settings.HouseAuthToken, requestId, (short)door)
			.HandleExceptionAndGetStringResult(it => it.IsSuccessStatusCode
				? $"Door ({door}) is opened"
				: $"Error [{it.StatusCode}]");
}

string GetUsers()
{
	var sb = new StringBuilder();

	if (Settings.HouseBotUsers.Count is 0)
		sb.AppendLine("Current users is 0");
	else
		sb.AppendLine("Current users:");

	for (var i = 0; i < Settings.HouseBotUsers.Count; i++)
	{
		var user = Settings.HouseBotUsers[i];

		sb.AppendLine($"{i} - {user.ToString()}");
	}

	return sb.ToString();
}

string GetRequestsToUsers()
{
	var sb = new StringBuilder();

	if (Settings.HouseBotRequestsToUsers.Count is 0)
		sb.AppendLine("Current requests to users is 0");
	else
		sb.AppendLine("Current requests to users:");

	for (var i = 0; i < Settings.HouseBotRequestsToUsers.Count; i++)
	{
		var user = Settings.HouseBotRequestsToUsers[i];

		sb.AppendLine($"{i} - {user.ToString()}");
	}

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
				return $"Error [{it.StatusCode}]";

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
				: $"Error [{it.StatusCode}]");
}

async Task<string> AcceptRequestToUsers(int id, CancellationToken cancellationToken)
{
	var user = Settings.HouseBotRequestsToUsers[id];

	if (user is null)
		return "The user does not exist";

	Settings.HouseBotUsers.Add(user);
	Settings.HouseBotRequestsToUsers.Remove(user);

	SaveSettings(Settings.FilePath, Settings);

	var sentMessage = await botClient.SendTextMessageAsync(
		chatId: user.Id,
		$"Request to users is accepted",
		allowSendingWithoutReply: true,
		cancellationToken: cancellationToken);

	return $"Request to users from {user} is accepted";
}

async Task<string> DeleteFromUsers(int id, CancellationToken cancellationToken)
{
	var user = Settings.HouseBotUsers[id];

	if (user is null)
		return "The user does not exist";

	Settings.HouseBotUsers.Remove(user);
	Settings.HouseBotRequestsToUsers.Remove(user);

	SaveSettings(Settings.FilePath, Settings);

	var sentMessage = await botClient.SendTextMessageAsync(
		chatId: user.Id,
		$"You have been removed from users",
		allowSendingWithoutReply: true,
		cancellationToken: cancellationToken);

	return $"{user} has been removed from the users";
}

string GenerateRequestId() => Guid.NewGuid().ToString().ToUpperInvariant();

enum Commands : byte
{
	RequestToUser,
	GetChatId,
	OpenEntranceDoor,
	OpenMainDoor,
	OpenNearShopDoor,
	OpenNearParkingDoor,
	GetUsers,
	GetRequestsToUsers,
	GenerateCode,
	DeleteCode,
	AcceptRequestToUsers,
	DeleteFromUsers
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

interface ILogger
{
	void Log(string text);
}

sealed class Logger : ILogger
{
	private readonly string _filePath;

	public Logger(string filePath)
	{
		_filePath = filePath;
	}

	public void Log(string text)
	{
		System.IO.File.AppendAllText(_filePath, text);
	}
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

sealed class Settings
{
	public string FilePath { get; set; } = string.Empty;

	public string HouseApiUrl { get; set; } = string.Empty;

	public string HouseAuthToken { get; set; } = string.Empty;

	public string HouseBotAccessToken { get; set; } = string.Empty;

	public List<User> HouseBotAdmins { get; set; } = new();

	public List<User> HouseBotUsers { get; set; } = new();

	public List<User> HouseBotRequestsToUsers { get; set; } = new();
}

sealed class User : IEquatable<User>
{
	public long Id { get; set; }

	public string FirstName { get; set; }

	public string LastName { get; set; }

	public string Username { get; set; }

	public override string ToString() => $"{FirstName} {LastName} @{Username} {Id}";

	public override bool Equals(object? obj)
	{
		if (obj is null)
			return false;

		if (GetType() != obj.GetType())
			return false;

		return Equals(obj as User);
	}

	public bool Equals(User? other)
		=> EqualityComparer<long>.Default.Equals(Id, other?.Id ?? default);

	public override int GetHashCode()
		=> HashCode.Combine(Id);
}