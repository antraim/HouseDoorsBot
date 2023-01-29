using Refit;

using System.Collections.ObjectModel;
using System.Text;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

const string HOUSE_API_URL = "HOUSE_API_URL";
const string HOUSE_AUTH_TOKEN = "HOUSE_AUTH_TOKEN";
const string HOUSE_BOT_ACCESS_TOKEN = "HOUSE_BOT_ACCESS_TOKEN";
const string HOUSE_BOT_USERS = "HOUSE_BOT_USERS";
const string ENVIRONMENT_VARIABLE_ERROR_MESSAGE = "Environment Variable is null or empty.";

var HouseApiUrl = Environment.GetEnvironmentVariable(HOUSE_API_URL)
	.ThrowIfNullOrWhiteSpace(HOUSE_API_URL, ENVIRONMENT_VARIABLE_ERROR_MESSAGE);
var HouseAuthToken = Environment.GetEnvironmentVariable(HOUSE_AUTH_TOKEN)
	.ThrowIfNullOrWhiteSpace(HOUSE_AUTH_TOKEN, ENVIRONMENT_VARIABLE_ERROR_MESSAGE);
var HouseBotAccessToken = Environment.GetEnvironmentVariable(HOUSE_BOT_ACCESS_TOKEN)
	.ThrowIfNullOrWhiteSpace(HOUSE_BOT_ACCESS_TOKEN, ENVIRONMENT_VARIABLE_ERROR_MESSAGE);
var HouseBotUsers = Environment.GetEnvironmentVariable(HOUSE_BOT_USERS)?.Split(',');

var CommandDoorDictionary = new ReadOnlyDictionary<string, Doors>(new Dictionary<string, Doors>
{
	{ "0", Doors.Entrance },
	{ "1", Doors.Main },
	{ "2", Doors.NearShop },
	{ "3", Doors.NearParking },
});

var botClient = new TelegramBotClient(HouseBotAccessToken);

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

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
	if (update.Message is not { } message)
		return;
	if (message.Text is not { } command)
		return;

	var chatId = message.Chat.Id;
	var user = $"{message.Chat.FirstName} {message.Chat.LastName} (@{message.Chat.Username})";

	Console.WriteLine($"Received a '{command}' message from {user}, chat {chatId}.");

	var result = await ExecuteCommandAsync(chatId, command);

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

async Task<string> ExecuteCommandAsync(long chatId, string command)
{
	if (command.Equals("/start"))
		return "Hello:)";

	if (HouseBotUsers is not null)
		if (!HouseBotUsers.Contains(chatId.ToString()))
			return "No access";

	var isExistCommand = CommandDoorDictionary.TryGetValue(command, out var door);
	var result = isExistCommand
		? await OpenDoorCommandAsync(door)
		: GetAvailableCommands();

	return result;
}

async Task<string> OpenDoorCommandAsync(Doors door)
{
	var api = RestService.For<IApi>(HouseApiUrl);
	var requestId = Guid.NewGuid().ToString().ToUpperInvariant(); //3ED00FC0-6E00-00C8-AD5A-8AD0A00A1F00

	try
	{
		var result = await api.OpenDoorAsync($"Bearer {HouseAuthToken}", requestId, (short)door);

		return result.IsSuccessStatusCode
			? $"Door ({door}) is opened [{result.StatusCode.ToString()}]"
			: $"Error [{result.StatusCode.ToString()}]";
	}
	catch (ApiException apiException)
	{
		return $"Exception ({apiException.Message})";
	}
}

string GetAvailableCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach(var command in CommandDoorDictionary)
		sb.AppendLine($"{command.Key} - Open {command.Value} door");

	return sb.ToString();
}

enum Doors : short
{
	Entrance = 6700,
	Main = 6701,
	NearShop = 13519,
	NearParking = 14149
}

interface IApi
{
	[Post("/app/devices/{doorId}/open")]
	Task<IApiResponse> OpenDoorAsync(
		[Header("Authorization")] string token,
		[Header("X-Request-Id")] string requestId,
		short doorId);
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