namespace Lane.Tools.Identity;

/// <summary>
/// The words a spoken proof phrase is drawn from.
///
/// Chosen for what survives a speech recogniser rather than for variety. Every one of them
/// is a common, concrete, mostly two-syllable English word, because a recogniser biased
/// towards ordinary conversation will quietly rewrite a rare word into a common one that
/// sounds like it — and a phrase that comes back rewritten is a phrase that does not redeem.
///
/// Left out on purpose: homophones a recogniser has no way to choose between (cellar and
/// seller, petal and pedal, carat and carrot), words whose spelling varies by country, words
/// that would come back as two whose letters do not add back up ("stir up" for stirrup), and
/// pairs too close to each other to survive a bad microphone.
///
/// Three drawn from 333 without replacement is 36,926,037 phrases — far past guessing,
/// against a window of ten minutes and at most thirty-two codes alive at once.
/// </summary>
internal static class ProofWords
{
    public static IReadOnlyList<string> All { get; } =
    [
        "anchor", "antler", "apple", "apron", "arrow", "attic", "avocado", "badger", "bagel",
        "balcony", "bamboo", "banana", "banjo", "barrel", "basket", "beacon", "beaver", "bicycle",
        "biscuit", "blanket", "blizzard", "blossom", "bobcat", "bonfire", "bookcase", "boulder",
        "bracket", "bramble", "bucket", "buffalo", "bugle", "bumper", "bunker", "burrow", "butter",
        "cabbage", "cabin", "cactus", "camel", "campfire", "candle", "canvas", "canyon", "caramel",
        "cargo", "carpet", "cashew", "castle", "cauldron", "cavern", "chapel", "cherry", "chestnut",
        "chimney", "chisel", "cinder", "cinnamon", "clover", "cobweb", "coconut", "comet", "compass",
        "copper", "cottage", "cougar", "coyote", "crayon", "cricket", "crimson", "crystal", "cupboard",
        "curtain", "cushion", "daffodil", "dagger", "daisy", "dandelion", "denim", "diamond",
        "dolphin", "domino", "donkey", "dragon", "drawer", "driftwood", "drumstick", "dumpling",
        "eagle", "ember", "engine", "envelope", "falcon", "fender", "fiddle", "flamingo", "flannel",
        "flipper", "forest", "fossil", "foxglove", "freckle", "garlic", "gazelle", "ginger", "giraffe",
        "glacier", "glitter", "gopher", "granite", "grapefruit", "gravel", "gremlin", "hammer",
        "hamster", "harvest", "hazel", "hedgehog", "helmet", "hickory", "hollow", "honey", "hopscotch",
        "hornet", "hummingbird", "iceberg", "igloo", "inkwell", "ivory", "jackal", "jasmine",
        "jigsaw", "juniper", "kangaroo", "kayak", "kennel", "kettle", "kitten", "koala", "ladder",
        "lagoon", "lantern", "lavender", "ledger", "lemon", "leopard", "lettuce", "lighthouse",
        "lilac", "lizard", "lobster", "locker", "lollipop", "magnet", "mammoth", "mandolin", "mango",
        "maple", "marble", "marigold", "marmalade", "mattress", "meadow", "melon", "mermaid",
        "mitten", "molasses", "monkey", "mosaic", "mountain", "muffin", "mushroom", "napkin",
        "nectar", "needle", "nickel", "nutmeg", "oatmeal", "octopus", "olive", "onion", "orbit",
        "orchid", "ostrich", "otter", "oyster", "paddle", "pancake", "panda", "pantry", "paprika",
        "parachute", "parsnip", "pasture", "peacock", "peanut", "pebble", "pelican", "penguin",
        "pepper", "pewter", "pickle", "picnic", "pigeon", "pillow", "pinecone", "pistachio", "planet",
        "plaza", "pocket", "polka", "poncho", "poodle", "poppy", "porridge", "pottery", "prairie",
        "pretzel", "pudding", "puffin", "pumpkin", "puppet", "pyramid", "quilt", "rabbit", "raccoon",
        "radish", "rafter", "rainbow", "rascal", "raven", "ribbon", "rocket", "rooster", "rosemary",
        "rubble", "ruby", "saddle", "saffron", "salmon", "sandal", "sapphire", "sardine", "satchel",
        "sawdust", "scarecrow", "scissors", "seagull", "seashell", "seaweed", "shadow", "shamrock",
        "shovel", "shutter", "silver", "siren", "skillet", "sleigh", "slipper", "snapdragon",
        "snorkel", "snowflake", "sofa", "spaniel", "sparrow", "spider", "spinach", "sponge", "spruce",
        "squirrel", "stadium", "stallion", "starfish", "sticker", "stopwatch", "sunflower", "sunset",
        "sweater", "syrup", "tadpole", "tambourine", "tangerine", "tapestry", "teapot", "thicket",
        "thimble", "thistle", "thunder", "ticket", "tiger", "tinsel", "toaster", "tomato", "topaz",
        "tornado", "tortoise", "towel", "tractor", "trellis", "trombone", "trophy", "trout", "trumpet",
        "tulip", "tundra", "tunnel", "turnip", "turtle", "tuxedo", "umbrella", "unicorn", "vanilla",
        "velvet", "vinegar", "violet", "volcano", "waffle", "wagon", "wallet", "walnut", "walrus",
        "warthog", "wasabi", "waterfall", "weasel", "whisker", "wildcat", "willow", "windmill",
        "wombat", "woodpecker", "zebra", "zigzag", "zucchini"
    ];
}
