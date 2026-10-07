using System.Globalization;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Localization;

/// <summary>
/// Panel texts in English and Romanian. English is the default and the fallback for any missing key.
/// The AI's own instructions stay in English; the AI answers in the language the user writes in.
/// </summary>
public sealed class UiText
{
    public const string English = "en";
    public const string Romanian = "ro";

    public static readonly IReadOnlyList<string> SupportedLanguages = [English, Romanian];

    private readonly IReadOnlyDictionary<string, string> _strings;

    public UiText(string language)
    {
        Language = IsSupported(language) ? language : English;
        _strings = Language == Romanian ? RomanianStrings : EnglishStrings;
        Culture = CultureInfo.GetCultureInfo(Language == Romanian ? "ro-RO" : "en-GB");
    }

    public string Language { get; }

    public CultureInfo Culture { get; }

    /// <summary>English name of the language, for the AI instructions.</summary>
    public string LanguageName => Language == Romanian ? "Romanian" : "English";

    public string this[string key] =>
        _strings.TryGetValue(key, out string? text) ? text
        : EnglishStrings.TryGetValue(key, out string? fallback) ? fallback
        : key;

    public static bool IsSupported(string? language) => language is not null && SupportedLanguages.Contains(language);

    public string Format(string key, params object[] args) => string.Format(Culture, this[key], args);

    public string SelectedElements(int count) => Format(PluralKey("Selection", count), count);

    public string ProgressFor(ITool tool) => _strings.TryGetValue("Progress." + tool.Name, out string? label) ? label : tool.ProgressLabel;

    /// <summary>
    /// English: one / other. Romanian: one; "few" for 0 and numbers ending in 01–19; "many" (with "de") otherwise.
    /// </summary>
    private string PluralKey(string stem, int count)
    {
        if (count == 1)
        {
            return stem + "One";
        }

        int lastTwo = count % 100;
        bool romanianMany = Language == Romanian && count != 0 && (lastTwo == 0 || lastTwo >= 20);
        return stem + (romanianMany ? "Many" : "Few");
    }

    internal static readonly IReadOnlyDictionary<string, string> EnglishStrings = new Dictionary<string, string>
    {
        ["Refresh"] = "Refresh",
        ["RefreshTip"] = "Read the current context from Revit",
        ["ApiKey"] = "API key",
        ["ApiKeyTip"] = "Add, change or remove your OpenAI API key",
        ["LanguageToggle"] = "Română",
        ["LanguageToggleTip"] = "Show the panel in Romanian",
        ["ConsentTitle"] = "Before you start",
        ["ConsentBody"] = "To answer questions, Revit AI sends what you type and parts of your model to OpenAI: element names, categories, types, parameter values, levels and views. OpenAI's API data policies apply. Nothing is sent until you accept.",
        ["ConsentAccept"] = "I understand, continue",
        ["KeyTitle"] = "OpenAI API key",
        ["KeyHelp"] = "Create one at platform.openai.com. It is stored encrypted for your Windows account and only sent to OpenAI.",
        ["Save"] = "Save",
        ["Remove"] = "Remove",
        ["Preview"] = "Preview",
        ["PreviewTip"] = "Run the plan in Revit and roll it back, to see the real result and warnings",
        ["Apply"] = "Apply",
        ["ApplyTip"] = "Make these changes. They become one undo step (Ctrl+Z).",
        ["Discard"] = "Discard",
        ["Send"] = "Send",
        ["Cancel"] = "Cancel",
        ["Footer"] = "Nothing in your model changes until you click Apply. Each applied plan is one undo step (Ctrl+Z).",
        ["You"] = "You",
        ["Welcome"] = "Ask me about your model or what to change, for example: \"What did I select?\", \"How many doors are on this level?\", \"Make this wall 50 cm longer.\" I only propose changes; nothing changes until you click Apply.",
        ["NoProject"] = "No project open",
        ["SelectionOne"] = "{0} selected element",
        ["SelectionFew"] = "{0} selected elements",
        ["SelectionMany"] = "{0} selected elements",
        ["ProjectChangedConversation"] = "The active project changed, so I started a new conversation.",
        ["ProjectChangedPlan"] = "The active project changed, so I discarded the proposal. Nothing was changed.",
        ["PasteKeyFirst"] = "Paste your OpenAI API key first.",
        ["KeySaved"] = "API key saved, encrypted for your Windows account.",
        ["KeySaveFailed"] = "I couldn't save the API key: {0}",
        ["KeyRemoved"] = "API key removed.",
        ["KeyRemoveFailed"] = "I couldn't remove the API key: {0}",
        ["AcceptNoticeFirst"] = "Please read and accept the data notice above first.",
        ["AddKeyFirst"] = "Add your OpenAI API key first, in the API key panel above.",
        ["PreviousPlanDiscarded"] = "I discarded the previous proposal; it was never applied.",
        ["Discarded"] = "Discarded the proposal. Nothing was changed.",
        ["Cancelled"] = "Cancelled.",
        ["OpenAiTimeout"] = "OpenAI didn't answer within {0} seconds. Please try again.",
        ["CouldNotReachOpenAi"] = "I couldn't reach OpenAI. Check your internet connection and try again.",
        ["SomethingWrong"] = "Something went wrong: {0}",
        ["AiInvalidKey"] = "OpenAI rejected the API key. Check it in the API key panel above.",
        ["AiRateLimited"] = "OpenAI's rate limit or quota was reached ({0}). Wait a moment, or check your OpenAI billing.",
        ["AiBadResponse"] = "OpenAI returned an answer I couldn't read. Please try again.",
        ["AiFailed"] = "The OpenAI request failed: {0}",
        ["RevitTimeout"] = "Revit didn't respond in time. It may be busy or showing a dialog; close it and try again.",
        ["ReadModelFailed"] = "I couldn't read the model: {0}",
        ["PlanHeader"] = "Proposed changes · not applied yet · operations: {0}",
        ["PlanPreviewRequired"] = " · preview required before Apply",
        ["StatusPreviewing"] = "Previewing in Revit. Nothing will be kept…",
        ["StatusApplying"] = "Applying changes in Revit…",
        ["PreviewTimeout"] = "Revit didn't start the preview in time (busy or showing a dialog). Nothing was changed. Try again.",
        ["ApplyTimeout"] = "Revit didn't start applying in time (busy or showing a dialog). Nothing was changed. Try again.",
        ["PreviewError"] = "Preview failed: {0}. Nothing was changed.",
        ["ApplyError"] = "Apply failed: {0}. Nothing was changed.",
        ["PreviewSucceeded"] = "Preview succeeded. It was rolled back, so nothing was kept:",
        ["PreviewFailedAtStep"] = "Preview failed at step {0}: {1}",
        ["PreviewAdjust"] = "Nothing was changed. Ask me to adjust the plan.",
        ["Applied"] = "Applied {0} change(s):",
        ["UndoHint"] = "Undo all of it with Ctrl+Z (\"{0}\").",
        ["ApplyFailedAtStep"] = "Nothing was changed. Step {0} ({1}) failed: {2}",
        ["StepsRolledBack"] = "Steps 1–{0} were rolled back too.",
        ["RevitWarning"] = "Revit warning: {0}",
        ["Thinking"] = "Thinking…",
        ["NoAnswer"] = "I don't have an answer for that.",
        ["StoppedAtLimit"] = "I stopped after {0} steps without reaching an answer. Try asking something more specific.",
    };

    internal static readonly IReadOnlyDictionary<string, string> RomanianStrings = new Dictionary<string, string>
    {
        ["Refresh"] = "Reîmprospătează",
        ["RefreshTip"] = "Citește contextul curent din Revit",
        ["ApiKey"] = "Cheie API",
        ["ApiKeyTip"] = "Adaugă, schimbă sau șterge cheia ta API OpenAI",
        ["LanguageToggle"] = "English",
        ["LanguageToggleTip"] = "Afișează panoul în engleză",
        ["ConsentTitle"] = "Înainte de a începe",
        ["ConsentBody"] = "Pentru a răspunde la întrebări, Revit AI trimite la OpenAI ce scrii și părți din modelul tău: nume de elemente, categorii, tipuri, valori ale parametrilor, niveluri și vederi. Se aplică politicile OpenAI privind datele trimise prin API. Nu se trimite nimic până nu accepți.",
        ["ConsentAccept"] = "Am înțeles, continuă",
        ["KeyTitle"] = "Cheie API OpenAI",
        ["KeyHelp"] = "Creează una pe platform.openai.com. Este stocată criptat pentru contul tău Windows și este trimisă doar către OpenAI.",
        ["Save"] = "Salvează",
        ["Remove"] = "Șterge",
        ["Preview"] = "Previzualizează",
        ["PreviewTip"] = "Rulează planul în Revit și anulează-l, ca să vezi rezultatul real și avertismentele",
        ["Apply"] = "Aplică",
        ["ApplyTip"] = "Fă aceste modificări. Devin un singur pas de anulare (Ctrl+Z).",
        ["Discard"] = "Renunță",
        ["Send"] = "Trimite",
        ["Cancel"] = "Oprește",
        ["Footer"] = "Nimic nu se schimbă în model până nu apeși Aplică. Fiecare plan aplicat este un singur pas de anulare (Ctrl+Z).",
        ["You"] = "Tu",
        ["Welcome"] = "Întreabă-mă despre model sau ce să modific, de exemplu: „Ce am selectat?”, „Câte uși sunt pe acest nivel?”, „Prelungește acest perete cu 50 cm.” Eu doar propun modificări; nimic nu se schimbă până nu apeși Aplică.",
        ["NoProject"] = "Niciun proiect deschis",
        ["SelectionOne"] = "{0} element selectat",
        ["SelectionFew"] = "{0} elemente selectate",
        ["SelectionMany"] = "{0} de elemente selectate",
        ["ProjectChangedConversation"] = "Proiectul activ s-a schimbat, așa că am început o conversație nouă.",
        ["ProjectChangedPlan"] = "Proiectul activ s-a schimbat, așa că am renunțat la propunere. Nu s-a modificat nimic.",
        ["PasteKeyFirst"] = "Lipește mai întâi cheia ta API OpenAI.",
        ["KeySaved"] = "Cheia API a fost salvată, criptată pentru contul tău Windows.",
        ["KeySaveFailed"] = "Nu am putut salva cheia API: {0}",
        ["KeyRemoved"] = "Cheia API a fost ștearsă.",
        ["KeyRemoveFailed"] = "Nu am putut șterge cheia API: {0}",
        ["AcceptNoticeFirst"] = "Te rog să citești și să accepți mai întâi notificarea despre date de mai sus.",
        ["AddKeyFirst"] = "Adaugă mai întâi cheia API OpenAI, în panoul Cheie API de mai sus.",
        ["PreviousPlanDiscarded"] = "Am renunțat la propunerea anterioară; nu a fost aplicată.",
        ["Discarded"] = "Am renunțat la propunere. Nu s-a modificat nimic.",
        ["Cancelled"] = "Oprit.",
        ["OpenAiTimeout"] = "OpenAI nu a răspuns în timpul alocat ({0} s). Te rog să încerci din nou.",
        ["CouldNotReachOpenAi"] = "Nu am putut contacta OpenAI. Verifică conexiunea la internet și încearcă din nou.",
        ["SomethingWrong"] = "Ceva nu a mers bine: {0}",
        ["AiInvalidKey"] = "OpenAI a respins cheia API. Verific-o în panoul Cheie API de mai sus.",
        ["AiRateLimited"] = "S-a atins limita de cereri sau cota OpenAI ({0}). Așteaptă puțin sau verifică facturarea OpenAI.",
        ["AiBadResponse"] = "OpenAI a trimis un răspuns pe care nu l-am putut citi. Te rog să încerci din nou.",
        ["AiFailed"] = "Cererea către OpenAI a eșuat: {0}",
        ["RevitTimeout"] = "Revit nu a răspuns la timp. Poate este ocupat sau afișează un dialog; închide-l și încearcă din nou.",
        ["ReadModelFailed"] = "Nu am putut citi modelul: {0}",
        ["PlanHeader"] = "Modificări propuse · neaplicate încă · operații: {0}",
        ["PlanPreviewRequired"] = " · previzualizarea este obligatorie înainte de Aplică",
        ["StatusPreviewing"] = "Previzualizare în Revit. Nu se va păstra nimic…",
        ["StatusApplying"] = "Se aplică modificările în Revit…",
        ["PreviewTimeout"] = "Revit nu a pornit previzualizarea la timp (este ocupat sau afișează un dialog). Nu s-a modificat nimic. Încearcă din nou.",
        ["ApplyTimeout"] = "Revit nu a pornit aplicarea la timp (este ocupat sau afișează un dialog). Nu s-a modificat nimic. Încearcă din nou.",
        ["PreviewError"] = "Previzualizarea a eșuat: {0}. Nu s-a modificat nimic.",
        ["ApplyError"] = "Aplicarea a eșuat: {0}. Nu s-a modificat nimic.",
        ["PreviewSucceeded"] = "Previzualizarea a reușit. A fost anulată, deci nu s-a păstrat nimic:",
        ["PreviewFailedAtStep"] = "Previzualizarea a eșuat la pasul {0}: {1}",
        ["PreviewAdjust"] = "Nu s-a modificat nimic. Cere-mi să ajustez planul.",
        ["Applied"] = "Am aplicat modificările ({0}):",
        ["UndoHint"] = "Anulezi totul cu Ctrl+Z („{0}”).",
        ["ApplyFailedAtStep"] = "Nu s-a modificat nimic. Pasul {0} ({1}) a eșuat: {2}",
        ["StepsRolledBack"] = "Au fost anulați și pașii 1–{0}.",
        ["RevitWarning"] = "Avertisment Revit: {0}",
        ["Thinking"] = "Mă gândesc…",
        ["NoAnswer"] = "Nu am un răspuns pentru asta.",
        ["StoppedAtLimit"] = "M-am oprit după {0} pași fără să ajung la un răspuns. Încearcă o întrebare mai precisă.",

        // Tool progress labels; English falls back to each tool's own ProgressLabel.
        ["Progress.get_project_info"] = "Citesc informațiile proiectului…",
        ["Progress.get_active_view"] = "Citesc vederea activă…",
        ["Progress.get_active_level"] = "Citesc nivelul activ…",
        ["Progress.get_selected_elements"] = "Citesc selecția…",
        ["Progress.get_element"] = "Citesc elementul…",
        ["Progress.find_elements"] = "Caut în model…",
        ["Progress.get_element_parameters"] = "Citesc parametrii…",
        ["Progress.find_family_types"] = "Caut tipurile disponibile…",
        ["Progress.get_project_standard_types"] = "Citesc tipurile standard ale proiectului…",
        ["Progress.find_nearby_elements"] = "Caut elementele din apropiere…",
        ["Progress.get_element_room"] = "Caut camera elementului…",
        ["Progress.get_room_boundary"] = "Citesc limitele camerei…",
        ["Progress.create_wall"] = "Verific peretele…",
        ["Progress.modify_wall"] = "Verific modificarea peretelui…",
        ["Progress.create_room"] = "Verific camera…",
        ["Progress.create_door"] = "Verific ușa…",
        ["Progress.create_window"] = "Verific fereastra…",
        ["Progress.create_floor"] = "Verific pardoseala…",
    };
}
