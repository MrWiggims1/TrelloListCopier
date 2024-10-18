using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using TrelloDotNet;
using TrelloDotNet.Model;
using TrelloDotNet.Model.Options.GetCardOptions;
using TrelloDotNet.Model.Options.MoveCardToBoardOptions;
using TrelloDotNet.Model.Options.MoveCardToListOptions;
using TrelloDotNet.Model.Search;

internal class Program
{
    private static TrelloClient trelloClient = default!;
    
    private static async Task Main(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();
        
        string? trelloApiKey = config.GetRequiredSection("TrelloApiKey").Value;
        string? trelloUserToken = config.GetRequiredSection("TrelloUserToken").Value;
        string?  templateBoard = config.GetRequiredSection("TemplateBoard").Value;

        List<string> targetListNames = config.GetRequiredSection("TargetListNames").Get<List<string>>() ?? throw new ArgumentNullException(nameof(targetListNames));
        List<string> destinationBoards = config.GetRequiredSection("DestinationBoards").Get<List<string>>() ?? throw new ArgumentNullException(nameof(destinationBoards));
        
        ArgumentNullException.ThrowIfNull(trelloApiKey);
        ArgumentNullException.ThrowIfNull(trelloUserToken);
        ArgumentNullException.ThrowIfNull(templateBoard);

        if (targetListNames.Distinct().Count() != targetListNames.Count)
            throw new ArgumentOutOfRangeException(nameof(targetListNames), "Target list names in configuration cannot have duplicate names");

        TrelloClientOptions options = new TrelloClientOptions()
        {
            AllowDeleteOfBoards = false,
            AllowDeleteOfOrganizations = false
        };
        
        trelloClient = new TrelloClient(trelloApiKey, trelloUserToken, options);

        Console.WriteLine("Application starting");
        
        Member? member = await trelloClient.GetTokenMemberAsync();
        
        if (member is null)
            throw new ArgumentException("Trello credentials are invalid");
        
        Console.WriteLine($"Logged in as {member.FullName}");

        string templateBoardId;

        SearchRequest searchRequest = new SearchRequest(templateBoard)
        {
            SearchBoards = true,
            BoardFields = new SearchRequestBoardFields("url")
        };
        
        SearchResult searchResult = await trelloClient.SearchAsync(searchRequest);

        if (searchResult.Boards.Count == 0)
        {
            throw new ArgumentException($"Could not find {templateBoard}");
        }
        
        if (searchResult.Boards.Count > 10)
        {
            throw new ArgumentException($"Too many boards called {templateBoard}");
        }
        
        if (searchResult.Boards.Count > 1)
        {
            Console.WriteLine($"### multiple boards called {templateBoard} ###");
            for (int i = 0; i < searchResult.Boards.Count; i++)
            {
                Console.WriteLine($"[{i}] - {searchResult.Boards[i].Url}");
            }

            Board? selectedBoard = null;

            while (selectedBoard is null or default(Board))
            {
                Console.Write("Select number with correct link: ");
                ConsoleKeyInfo response = Console.ReadKey();

                if (char.IsDigit(response.KeyChar))
                {
                    selectedBoard = searchResult.Boards.ElementAtOrDefault(int.Parse(response.KeyChar.ToString()));
                }
            }

            templateBoardId = selectedBoard.Id;
        }

        else
        {
            templateBoardId = searchResult.Boards.First().Id;
        }

        GetCardOptions cardOptions = new GetCardOptions()
        {
            IncludeList = true
        };

        List<Card> templateCards = await trelloClient.GetCardsOnBoardAsync(templateBoardId, cardOptions);
        List<List> templateLists = await trelloClient.GetListsOnBoardAsync(templateBoardId);
        
        templateLists = templateLists.OrderBy(x => x.Position).ToList();
        templateCards = templateCards.Where(x => templateLists.Select(l => l.Id).Contains(x.List.Id)).ToList();

        if (!templateCards.Any())
            throw new ArgumentException($"No cards will be copied from {templateBoard}");
        
        if (!templateLists.Any())
            throw new ArgumentException($"No lists will be copied from {templateBoard}");

        Console.WriteLine("###############################");
        Console.WriteLine($"Chosen lists.");

        
        foreach (List list in templateLists)
        {
            if (targetListNames.Contains(list.Name))
            {
                Console.ForegroundColor = ConsoleColor.Green;
            }           
            else
                Console.ForegroundColor = ConsoleColor.Red;
            
            Console.WriteLine(list.Name);
        }
        Console.ResetColor();
        
        Console.WriteLine("Destination boards:");

        foreach (string board in destinationBoards)
        {
            Console.WriteLine($" - {board}");
        }
        
        Console.WriteLine("###############################");
        Console.WriteLine("Press y to confirm and continue");
        
        if(Console.ReadKey().KeyChar != 'y')
            return;

        Console.WriteLine();

        List<string> destinationIds = [];

        foreach (string destinationBoard in destinationBoards)
        {
            SearchRequest search = new SearchRequest(destinationBoard);

            SearchResult result = await trelloClient.SearchAsync(search);
            
            if(result.Boards.Count == 1)
                destinationIds.Add(result.Boards.First().Id);
            
            else if (result.Boards.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Skipping {destinationBoard} - could not find board");
                Console.ResetColor();
            }

            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Skipping {destinationBoard} - found {result.Boards.Count} with same name");
                Console.ResetColor();
            }
        }

        Console.WriteLine($"Found {destinationIds.Count} out of {destinationBoards.Count} boards");
        Console.WriteLine("Starting to copy lists");

        foreach (string id in destinationIds)
        {
            await CopyListsOnBoard(id, templateLists, templateCards);
        }
    }

    static async Task CopyListsOnBoard(string targetBoardId, List<List> lists, List<Card> cards)
    {
        Dictionary<string, string> listIdConvertion = [];

        List spacerList = new List("New Template Lists ->", targetBoardId)
        {
            NamedPosition = NamedPosition.Bottom
        };

        await trelloClient.AddListAsync(spacerList);

        foreach (List list in lists)
        {
           List? newList = await trelloClient.AddListAsync(
                               new List(list.Name, targetBoardId)
                                   {
                                       NamedPosition = NamedPosition.Bottom
                                   }
                               );
           
           listIdConvertion.Add(list.Id, newList.Id);
        }

        foreach (Card card in cards.OrderBy(x => x.Position))
        {
            var cardMoveOptions = new MoveCardToBoardOptions()
            {
                LabelOptions = MoveCardToBoardOptionsLabelOptions.MigrateToLabelsOfSameNameAndRemoveMissing,
                MemberOptions = MoveCardToBoardOptionsMemberOptions.KeepMembersAlsoOnNewBoardAndRemoveRest,
                NamedPositionOnNewList = NamedPosition.Bottom,
                NewListId = listIdConvertion[card.List.Id]
            };

            Card newCard = await trelloClient.AddCardAsync(card);
            await trelloClient.MoveCardToBoard(newCard.Id, targetBoardId, cardMoveOptions);
        }
    }
}