using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Core.Player;

public enum ListingKind { Item, Pokemon }

public enum MarketSort { Newest, PriceLow, PriceHigh, EndingSoon }

/// <summary>A fixed-price listing on the Global Market: a stack of items or one Pokémon.</summary>
public sealed record MarketListing(int Id, string Seller, ListingKind Kind, int Item, int Count, Pokemon? Pokemon, int Price,
    DateTime Listed, DateTime Expires);

public sealed record MarketQuery(ListingKind? Kind = null, string Text = "", int MaxPrice = 0, MarketSort Sort = MarketSort.Newest);

public enum BuyResult { Bought, NotFound, OwnListing, NotEnoughMoney, Expired }

/// <summary>
/// The Global Market (BUILD_SPEC §7.7): fixed-price listings for 1–7 days, a listing fee and a sales tax as money sinks,
/// and delivery by mail. Online the server owns it and settles every sale in one transaction; offline this class is the
/// whole market, with listings seeded from sample sellers.
/// </summary>
public sealed class Market
{
    /// <summary>Listing fee: 2% of the price (at least ₽10), paid up front and not refunded.</summary>
    public const double ListingFeeRate = 0.02;
    public const int MinListingFee = 10;
    /// <summary>Sales tax: 5% of the price, taken from the seller's proceeds.</summary>
    public const double SalesTaxRate = 0.05;
    public const int MinDays = 1, MaxDays = 7, MaxPrice = 9_999_999, MaxListingsPerSeller = 20;

    readonly List<MarketListing> _listings = new();
    int _nextId = 1;

    public IReadOnlyList<MarketListing> Listings => _listings;

    public static int ListingFee(int price) => Math.Max(MinListingFee, (int)Math.Ceiling(price * ListingFeeRate));
    public static int Proceeds(int price) => price - (int)Math.Ceiling(price * SalesTaxRate);

    /// <summary>
    /// Lists something for sale. The caller has already taken it from the seller and charged <see cref="ListingFee"/>.
    /// Returns why it can't be listed, or null when it was (the listing is <paramref name="listing"/>).
    /// </summary>
    public string? List(string seller, ListingKind kind, int item, int count, Pokemon? pokemon, int price, int days, DateTime now, out MarketListing? listing)
    {
        listing = null;
        if (price is <= 0 or > MaxPrice)
            return $"Prices run from ₽1 to ₽{MaxPrice:N0}.";
        if (days is < MinDays or > MaxDays)
            return $"Listings last {MinDays}–{MaxDays} days.";
        if (kind == ListingKind.Pokemon ? pokemon is null : item <= 0 || count <= 0)
            return "Nothing to list.";
        if (_listings.Count(l => l.Seller == seller) >= MaxListingsPerSeller)
            return $"You can have at most {MaxListingsPerSeller} listings.";
        listing = new MarketListing(_nextId++, seller, kind, item, count, pokemon, price, now, now.AddDays(days));
        _listings.Add(listing);
        return null;
    }

    /// <summary>Listings matching <paramref name="query"/>; <paramref name="nameOf"/> names a listing for text search.</summary>
    public IEnumerable<MarketListing> Search(MarketQuery query, Func<MarketListing, string> nameOf, DateTime now)
    {
        var found = _listings.Where(l => l.Expires > now
            && (query.Kind is null || l.Kind == query.Kind)
            && (query.MaxPrice <= 0 || l.Price <= query.MaxPrice)
            && (query.Text.Length == 0 || nameOf(l).Contains(query.Text, StringComparison.OrdinalIgnoreCase)));
        return query.Sort switch
        {
            MarketSort.PriceLow => found.OrderBy(l => l.Price),
            MarketSort.PriceHigh => found.OrderByDescending(l => l.Price),
            MarketSort.EndingSoon => found.OrderBy(l => l.Expires),
            _ => found.OrderByDescending(l => l.Listed).ThenByDescending(l => l.Id),
        };
    }

    /// <summary>
    /// Buys listing <paramref name="id"/>: checks the buyer can pay, removes it, and charges <paramref name="money"/>.
    /// The caller mails the goods to the buyer and <see cref="Proceeds"/> to the seller.
    /// </summary>
    public BuyResult Buy(int id, string buyer, ref int money, DateTime now, out MarketListing? listing)
    {
        listing = _listings.FirstOrDefault(l => l.Id == id);
        if (listing is null)
            return BuyResult.NotFound;
        if (listing.Expires <= now)
            return BuyResult.Expired;
        if (listing.Seller == buyer)
            return BuyResult.OwnListing;
        if (money < listing.Price)
            return BuyResult.NotEnoughMoney;
        money -= listing.Price;
        _listings.Remove(listing);
        return BuyResult.Bought;
    }

    /// <summary>Takes down one of the seller's listings; the caller mails the goods back. The fee isn't refunded.</summary>
    public MarketListing? Cancel(int id, string seller)
    {
        var listing = _listings.FirstOrDefault(l => l.Id == id && l.Seller == seller);
        if (listing is not null)
            _listings.Remove(listing);
        return listing;
    }

    /// <summary>Removes listings past their end date and returns them, so the goods can be mailed back unsold.</summary>
    public List<MarketListing> Expire(DateTime now)
    {
        var expired = _listings.Where(l => l.Expires <= now).ToList();
        _listings.RemoveAll(l => l.Expires <= now);
        return expired;
    }
}
