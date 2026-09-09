// SPDX-License-Identifier: MIT
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
namespace AgentEval.VitrineDemo.Evals;
internal sealed class CatalogueContractSnapshot {
    private readonly string[] _productSkus;
    private readonly string[] _personaIds;
    private readonly string[] _toolNames;
    private CatalogueContractSnapshot(IEnumerable<string> productSkus, IEnumerable<string> personaIds,
        IEnumerable<string> toolNames, string? removedProductSku = null) {
        _productSkus = productSkus.ToArray();
        _personaIds = personaIds.ToArray();
        _toolNames = toolNames.ToArray();
        RemovedProductSku = removedProductSku;
    }
    public int ProductCount => _productSkus.Length;
    public int PersonaCount => _personaIds.Length;
    public int ToolCount => _toolNames.Length;
    public string? RemovedProductSku { get; }
    public static CatalogueContractSnapshot Capture() {
        var tools = RecommendationAgentFactory.BuildReadOnlyTools()
            .Concat(RecommendationAgentFactory.BuildApprovalGatedCommitTools())
            .Select(tool => tool.Name);
        return new(Catalogue.Default.All.Select(product => product.Sku), Personas.AllPersonaIds, tools);
    }
    public CatalogueContractSnapshot WithOneProductRemoved() {
        if (_productSkus.Length == 0)
            throw new InvalidOperationException("Cannot ablate an empty catalogue snapshot.");
        var removed = _productSkus[^1];
        return new(_productSkus.Where(sku => !string.Equals(sku, removed, StringComparison.Ordinal)),
            _personaIds, _toolNames, removed);
    }
    public bool ContainsProduct(string sku) => _productSkus.Contains(sku, StringComparer.Ordinal);
    public string Observe() => $"products={ProductCount};personas={PersonaCount};tools={ToolCount}";
}
