using Manatee.Trello;
using Microsoft.Extensions.Configuration;

internal class Program
{
    private static async Task Main(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        string? trelloApiKey    = config.GetRequiredSection("TrelloApiKey").Value;
        string? trelloUserToken = config.GetRequiredSection("TrelloUserToken").Value;
        string? templateBoard   = config.GetRequiredSection("TemplateBoard").Value;
        bool ignoreTargetLists  = config.GetRequiredSection("IgnoreTargetLists").Get<bool>();

        List<string> targetListNames       = config.GetRequiredSection("TargetListNames").Get<List<string>>() ??
                                             throw new ArgumentNullException(nameof(targetListNames));
        
        List<string> destinationBoardNames = config.GetRequiredSection("destinationBoardNames").Get<List<string>>() ??
                                             throw new ArgumentNullException(nameof(destinationBoardNames));

        ArgumentNullException.ThrowIfNull(trelloApiKey);
        ArgumentNullException.ThrowIfNull(trelloUserToken);
        ArgumentNullException.ThrowIfNull(templateBoard);

        if (targetListNames.Distinct().Count() != targetListNames.Count)
            throw new ArgumentOutOfRangeException(nameof(targetListNames), "Target list names in configuration cannot have duplicate names");

        if (destinationBoardNames.Distinct().Count() != destinationBoardNames.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationBoardNames), "Destination board names in configuration cannot have duplicate names");

        Console.WriteLine("Application starting");

        TrelloAuthorization.Default.AppKey    = trelloApiKey;
        TrelloAuthorization.Default.UserToken = trelloUserToken;

        TrelloFactory factory = new TrelloFactory();
        IMe? trelloClient = await factory.Me(TrelloAuthorization.Default);

        if (trelloClient is null)
            throw new ArgumentException("Trello credentials are invalid");

        Console.WriteLine($"Logged in as {trelloClient.FullName}");

        Board.DownloadedFields |= Board.Fields.Lists;

        List.DownloadedFields |= List.Fields.Cards;
        List.DownloadedFields |= List.Fields.Position;

        Card.DownloadedFields |= Card.Fields.List;
        Card.DownloadedFields |= Card.Fields.Position;


        ISearch? templateSearch = factory.Search(templateBoard, null, SearchModelType.Boards, null, false, TrelloAuthorization.Default);

        await templateSearch.Refresh();

        IBoard? selectedBoard = null;

        if (templateSearch.Boards.Count() == 0) 
            throw new ArgumentException($"Could not find {templateBoard}");

        if (templateSearch.Boards.Count() > 10) 
            throw new ArgumentException($"Too many boards called {templateBoard}");

        if (templateSearch.Boards.Count() > 1)
        {
            Console.WriteLine($"### multiple boards called {templateBoard} ###");
            for (int i = 0; i < templateSearch.Boards.Count(); i++)
            {
                Console.WriteLine(
                    $"[{i}] - {templateSearch.Boards.ElementAt(i).Name}: {templateSearch.Boards.ElementAt(i).Url}");
            }

            while (selectedBoard is null or default(Board))
            {
                Console.Write("Select number with correct link: ");
                ConsoleKeyInfo response = Console.ReadKey();

                if (char.IsDigit(response.KeyChar))
                    selectedBoard = templateSearch.Boards.ElementAtOrDefault(int.Parse(response.KeyChar.ToString()));
            }
        }

        else
        {
            selectedBoard = templateSearch.Boards.First();
        }

        await selectedBoard.Refresh();

        List<IList> templateLists = selectedBoard.Lists.ToList();

        templateLists = templateLists.OrderBy(x => x.Position).ToList();

        if (!templateLists.Any())
            throw new ArgumentException($"No lists will be copied from {templateBoard}");

        Console.WriteLine("###############################");
        Console.WriteLine($"{selectedBoard.Name} found with {templateLists.Count()} lists:");

        foreach (List list in templateLists)
        {
            if (ignoreTargetLists ? !targetListNames.Contains(list.Name) : targetListNames.Contains(list.Name))
                Console.ForegroundColor = ConsoleColor.Green;
            else
                Console.ForegroundColor = ConsoleColor.Red;

            Console.WriteLine(list.Name);
        }

        Console.ResetColor();

        Console.WriteLine("Destination boards:");

        foreach (string board in destinationBoardNames)
        {
            Console.WriteLine($" - {board}");
        }

        Console.WriteLine("###############################");
        Console.WriteLine("Press y to confirm and continue");

        if (!(Console.ReadKey().KeyChar == 'y' || Console.ReadKey().KeyChar != 'Y'))
        {
            Console.WriteLine();
            Console.WriteLine("Exiting program");
            Console.WriteLine("Press any key to exit");
            Console.ReadKey();
            return;
        }

        Console.WriteLine();

        templateLists = templateLists.Where(x => ignoreTargetLists ? !targetListNames.Contains(x.Name) : targetListNames.Contains(x.Name)).ToList();

        List<IBoard> destinationBoards = [];

        foreach (string name in destinationBoardNames)
        {
            ISearch? search = factory.Search(name, null, SearchModelType.Boards, null, false,
                                             TrelloAuthorization.Default);

            await search.Refresh();

            if (search.Boards.Count() == 1)
            {
                destinationBoards.Add(search.Boards.First());
            }

            else if (search.Boards.Count(x => x.Name == name) == 1)
            {
                destinationBoards.Add(search.Boards.First(x => x.Name == name));
            }

            else if (search.Boards.Count() == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Skipping {name} - could not find board");
                Console.ResetColor();
            }

            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Skipping {name} - found {search.Boards.Count()} with same name");
                Console.ResetColor();
            }
        }

        Console.WriteLine($"Found {destinationBoards.Count} out of {destinationBoardNames.Count} boards");
        Console.WriteLine("Starting to copy lists");

        foreach (IList list in templateLists)
        {
            await list.Refresh();
        }

        ParallelOptions pOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = default
        };

        ParallelQuery<IList> parallelTemplateNames = templateLists.AsParallel();

        await Parallel.ForEachAsync(destinationBoards.AsParallel(),
                                    pOptions,
                                    async (board, token) => await CopyListsOnBoard(board, parallelTemplateNames));

        Console.WriteLine("Done!");
        Console.WriteLine("Press any key to exit");
        Console.ReadKey();
    }

    private static async Task CopyListsOnBoard(IBoard targetBoard, ParallelQuery<IList> lists)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"Starting {targetBoard.Name}");
        Console.ResetColor();

        await targetBoard.Lists.Add("New Lists ->", Position.Bottom);

        foreach (IList list1 in lists)
        {
            List list = (List)list1;

            await CopyList(targetBoard, list);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Finished {targetBoard.Name}");
        Console.ResetColor();
    }

    private static async Task CopyList(IBoard targetBoard, IList list)
    {
        IList? newList = await targetBoard.Lists.Add(list.Name, Position.Bottom);

        foreach (ICard card in list.Cards.OrderBy(x => x.Position))
        {
            await newList.Cards.Add(card, CardCopyKeepFromSourceOptions.All);
        }

        await newList.Refresh();

        if (newList.Cards.Count() != list.Cards.Count())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"An error occured while copying {list.Name} to {targetBoard.Name}. {newList.Cards.Count()} out of {list.Cards.Count()} were copied. Please manually fix");
            Console.ResetColor();
        }
    }
}