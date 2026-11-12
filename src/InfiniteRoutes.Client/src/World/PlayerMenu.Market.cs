using Godot;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Client.World;

/// <summary>The Market tab: Buy (search the Global Market), Sell (list items or Pokémon, take listings down), Trade (offers between two players).</summary>
public sealed partial class PlayerMenu
{
    Control MarketTabPage()
    {
        _market = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _market.AddChild(BuyPage());
        _sellBox = Ui.Ui.VBox(8);
        var sell = Scroll(_sellBox);
        sell.Name = "Sell";
        _market.AddChild(sell);
        _tradeBox = Ui.Ui.VBox(8);
        var trade = Scroll(_tradeBox);
        trade.Name = "Trade";
        _market.AddChild(trade);
        _market.TabChanged += tab => RefreshMarket((int)tab);
        _market.Name = "Market";
        return _market;
    }

    // ---- Buy --------------------------------------------------------------------------------------------------------

    Control BuyPage()
    {
        _search = new LineEdit { PlaceholderText = "Item, Pokémon or seller", CustomMinimumSize = new Vector2(280, 0) };
        _search.TextSubmitted += _ => Search();
        _kind = new OptionButton();
        foreach (var k in new[] { "Everything", "Items", "Pokémon" })
            _kind.AddItem(k);
        _sort = new OptionButton();
        foreach (var k in new[] { "Newest", "Cheapest", "Dearest", "Ending soon" })
            _sort.AddItem(k);
        _maxPrice = Number(0, Market.MaxPrice, 0, 130);
        _maxPrice.TooltipText = "Highest price (0 = any)";
        _buyRows = Ui.Ui.VBox(4);
        var page = Ui.Ui.VBox(8,
            Ui.Ui.HBox(8, _search, _kind, _sort, Ui.Ui.Text("max ₽", 14, Ui.Ui.Muted), _maxPrice, Ui.Ui.Button("Search", Search)),
            Scroll(_buyRows));
        page.Name = "Buy";
        return page;
    }

    void Search() => Do("market-search", ("text", _search.Text.Trim()), ("kind", _kind.Selected switch { 1 => "item", 2 => "pokemon", _ => "" }),
        ("sort", _sort.Selected switch { 1 => "price", 2 => "price-desc", 3 => "ending", _ => "" }), ("max", ((int)_maxPrice.Value).ToString()));

    static string Listing(ListingRow l) => l.Pokemon is not null ? Mon(l.Pokemon) : $"{ItemName(l.Item)} ×{l.Count}";

    public void SetMarket(MarketPage page)
    {
        if (page.Mine)
        {
            _mine = page;
            if (_market.CurrentTab == Sell)
                ShowSell();
            return;
        }
        LastSearch = page;
        Ui.Ui.ClearChildren(_buyRows);
        if (page.Listings.Count == 0)
            _buyRows.AddChild(Ui.Ui.Text("Nothing for sale matches.", 15, Ui.Ui.Muted));
        var money = _state()?.Progress.Money ?? 0;
        foreach (var l in page.Listings)
        {
            var id = l.Id;
            var mine = string.Equals(l.Seller, _me, StringComparison.OrdinalIgnoreCase);
            var row = Ui.Ui.HBox(10, Wrapped($"{Listing(l)}   ₽{l.Price:N0}   from {l.Seller} · {Left(l.Expires)}"),
                Small(mine ? "Yours" : "Buy", () => Do("market-buy", ("id", id.ToString())), !mine && money >= l.Price));
            _buyRows.AddChild(row);
        }
    }

    // ---- Sell -------------------------------------------------------------------------------------------------------

    void ShowSell()
    {
        Ui.Ui.ClearChildren(_sellBox);
        var state = _state();
        _sellBox.AddChild(Ui.Ui.Text($"Listing costs a {Market.ListingFeeRate:P0} fee (at least ₽{Market.MinListingFee}), paid now; a sale pays you the price less {Market.SalesTaxRate:P0} tax, by mail.", 14, Ui.Ui.Muted));

        // An item stack.
        var bag = state?.Bag ?? [];
        var item = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        foreach (var b in bag)
            item.AddItem($"{ItemName(b.Item)} ×{b.Count}");
        var count = Number(1, 999, 1, 90);
        var price = Number(1, Market.MaxPrice, 500, 130);
        var days = Number(Market.MinDays, Market.MaxDays, 3, 70);
        var fee = Ui.Ui.Text("", 14, Ui.Ui.Muted);
        void Fee() => fee.Text = $"fee ₽{Market.ListingFee((int)price.Value):N0}";
        price.ValueChanged += _ => Fee();
        Fee();
        _sellBox.AddChild(Ui.Ui.Text("Sell items", 17, Ui.Ui.Accent));
        _sellBox.AddChild(Ui.Ui.HBox(8, item, Ui.Ui.Text("×", 14), count, Ui.Ui.Text("for ₽", 14), price, Ui.Ui.Text("days", 14), days, fee,
            Small("List", () =>
            {
                if (item.Selected >= 0 && item.Selected < bag.Count)
                    Do("market-sell", ("item", bag[item.Selected].Item.ToString()), ("count", ((int)count.Value).ToString()),
                        ("price", ((int)price.Value).ToString()), ("days", ((int)days.Value).ToString()));
            }, bag.Count > 0)));

        // A Pokémon.
        var mons = MyPokemon();
        var mon = new OptionButton { CustomMinimumSize = new Vector2(340, 0) };
        foreach (var m in mons)
            mon.AddItem(m.Label);
        var monPrice = Number(1, Market.MaxPrice, 5000, 130);
        var monDays = Number(Market.MinDays, Market.MaxDays, 3, 70);
        _sellBox.AddChild(Ui.Ui.Text("Sell a Pokémon", 17, Ui.Ui.Accent));
        _sellBox.AddChild(Ui.Ui.HBox(8, mon, Ui.Ui.Text("for ₽", 14), monPrice, Ui.Ui.Text("days", 14), monDays,
            Small("List", () =>
            {
                if (mon.Selected >= 0 && mon.Selected < mons.Count)
                    Do("market-sell", ("where", mons[mon.Selected].Where), ("index", mons[mon.Selected].Index.ToString()),
                        ("price", ((int)monPrice.Value).ToString()), ("days", ((int)monDays.Value).ToString()));
            }, mons.Count > 0)));

        // The player's listings.
        _sellBox.AddChild(Ui.Ui.Text($"Your listings ({_mine?.Listings.Count ?? 0}/{Market.MaxListingsPerSeller})", 17, Ui.Ui.Accent));
        foreach (var l in _mine?.Listings ?? [])
        {
            var id = l.Id;
            _sellBox.AddChild(Ui.Ui.HBox(10, Wrapped($"{Listing(l)}   ₽{l.Price:N0} · {Left(l.Expires)}"),
                Small("Take down", () => Do("market-cancel", ("id", id.ToString())))));
        }
    }

    // ---- Trade ------------------------------------------------------------------------------------------------------

    /// <summary>Prefills a trade offer to <paramref name="name"/> (from the friends list).</summary>
    public void TradeWith(string name)
    {
        OpenTab(MarketTab, Trade);
        _tradeTo.Text = name;
    }

    void ShowTrade()
    {
        var to = _tradeTo?.Text ?? "";
        Ui.Ui.ClearChildren(_tradeBox);
        var bag = _state()?.Bag ?? [];
        var mons = MyPokemon();

        _tradeBox.AddChild(Ui.Ui.Text("Make an offer", 17, Ui.Ui.Accent));
        _tradeTo = new LineEdit { PlaceholderText = "To (character name)", Text = to, CustomMinimumSize = new Vector2(220, 0) };
        var giveMoney = Number(0, Market.MaxPrice, 0, 120);
        var giveItem = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        giveItem.AddItem("(no item)");
        foreach (var b in bag)
            giveItem.AddItem($"{ItemName(b.Item)} ×{b.Count}");
        var giveCount = Number(1, 999, 1, 80);
        var giveMon = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        giveMon.AddItem("(no Pokémon)");
        foreach (var m in mons)
            giveMon.AddItem(m.Label);
        var askMoney = Number(0, Market.MaxPrice, 0, 120);
        var askItem = Number(0, 700, 0, 90);
        var askItemName = Ui.Ui.Text("(no item)", 14);
        askItem.ValueChanged += v => askItemName.Text = v > 0 ? ItemName((int)v) : "(no item)";
        var askCount = Number(1, 999, 1, 80);
        var askMon = new OptionButton();
        foreach (var k in new[] { "no Pokémon", "any Pokémon", "a Pokémon of species #" })
            askMon.AddItem(k);
        var askSpecies = Number(1, 649, 495, 90);
        var askSpeciesName = Ui.Ui.Text(SpeciesName(495), 14);
        askSpecies.ValueChanged += v => askSpeciesName.Text = SpeciesName((int)v);
        askSpecies.Visible = askSpeciesName.Visible = false;
        askMon.ItemSelected += i => askSpecies.Visible = askSpeciesName.Visible = i == 2;
        var note = new LineEdit { PlaceholderText = "Note (optional)", MaxLength = 120, CustomMinimumSize = new Vector2(360, 0) };
        _tradeBox.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("To", 14, Ui.Ui.Muted), _tradeTo, note));
        _tradeBox.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("You give  ₽", 14, Ui.Ui.Muted), giveMoney, giveItem, Ui.Ui.Text("×", 14), giveCount, giveMon));
        _tradeBox.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("You ask   ₽", 14, Ui.Ui.Muted), askMoney, Ui.Ui.Text("item #", 14), askItem, askItemName,
            Ui.Ui.Text("×", 14), askCount, askMon, askSpecies, askSpeciesName));
        _tradeBox.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text($"What you give is held until they answer (up to {3} days), then comes back by mail if they don't take it.", 13, Ui.Ui.Muted),
            Ui.Ui.Spacer(), Small("Send offer", () =>
            {
                var args = new List<(string, string)> { ("to", _tradeTo.Text.Trim()), ("give-money", ((int)giveMoney.Value).ToString()),
                    ("ask-money", ((int)askMoney.Value).ToString()), ("note", note.Text) };
                if (giveItem.Selected > 0)
                    args.AddRange([("give-item", bag[giveItem.Selected - 1].Item.ToString()), ("give-count", ((int)giveCount.Value).ToString())]);
                if (giveMon.Selected > 0)
                    args.AddRange([("give-where", mons[giveMon.Selected - 1].Where), ("give-index", mons[giveMon.Selected - 1].Index.ToString())]);
                if (askItem.Value > 0)
                    args.AddRange([("ask-item", ((int)askItem.Value).ToString()), ("ask-count", ((int)askCount.Value).ToString())]);
                args.Add(("ask-species", askMon.Selected switch { 1 => "0", 2 => ((int)askSpecies.Value).ToString(), _ => "-1" }));
                Do("trade-offer", [.. args]);
            })));

        // Offers to the player: what they'd get, what it asks, a Pokémon to send if it asks for one.
        _tradeBox.AddChild(Ui.Ui.Text($"Offers to you ({_trades?.Incoming.Count ?? 0})", 17, Ui.Ui.Accent));
        foreach (var t in _trades?.Incoming ?? [])
        {
            var id = t.Id;
            var row = Ui.Ui.HBox(8, Wrapped($"{t.From} gives {Goods(t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon)}  ⇄  asks {Asked(t)}"
                + (t.Note.Length > 0 ? $"  “{t.Note}”" : "") + $" · {Left(t.Expires)}"));
            OptionButton? pick = null;
            if (t.AskSpecies >= 0)
            {
                pick = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
                foreach (var m in mons)
                    pick.AddItem(m.Label);
                row.AddChild(pick);
            }
            row.AddChild(Small("Accept", () =>
            {
                if (pick is not null && pick.Selected >= 0 && pick.Selected < mons.Count)
                    Do("trade-accept", ("id", id.ToString()), ("where", mons[pick.Selected].Where), ("index", mons[pick.Selected].Index.ToString()));
                else
                    Do("trade-accept", ("id", id.ToString()));
            }));
            row.AddChild(Small("Decline", () => Do("trade-decline", ("id", id.ToString()))));
            _tradeBox.AddChild(row);
        }
        _tradeBox.AddChild(Ui.Ui.Text($"Your offers ({_trades?.Outgoing.Count ?? 0})", 17, Ui.Ui.Accent));
        foreach (var t in _trades?.Outgoing ?? [])
        {
            var id = t.Id;
            _tradeBox.AddChild(Ui.Ui.HBox(8, Wrapped($"To {t.To}: {Goods(t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon)}  ⇄  {Asked(t)} · {Left(t.Expires)}"),
                Small("Withdraw", () => Do("trade-cancel", ("id", id.ToString())))));
        }
    }

    static string Asked(TradeRow t)
    {
        var goods = Goods(t.AskMoney, t.AskItem, t.AskCount, null);
        var mon = t.AskSpecies switch { < 0 => "", 0 => "any Pokémon", _ => $"a {SpeciesName(t.AskSpecies)}" };
        return mon.Length == 0 ? goods : goods == "nothing" ? mon : $"{goods} + {mon}";
    }

    public void SetTrades(TradeList trades)
    {
        _trades = trades;
        if (_tabs.CurrentTab == MarketTab && _market.CurrentTab == Trade)
            ShowTrade();
    }
}
