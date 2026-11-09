namespace GtViewer.Web.Models.FilaAcoes;

public static class EstadosAcao
{
    public const string Pendente = "pendente";
    public const string Enviada = "enviada";
    public const string Executada = "executada";
    public const string Falhou = "falhou";
    public const string ResultadoDesconhecido = "resultado_desconhecido";
    public const string Cancelada = "cancelada";
}

public static class TiposAcao
{
    public const string GarantirResgate = "garantir_resgate";
    public const string RedefinirResgate = "redefinir_resgate";
    public const string TrocarResgate = "trocar_resgate";
    public const string Bloquear = "bloquear";
    public const string Desbloquear = "desbloquear";
    public const string Instalar = "instalar";
    public const string Remover = "remover";
    public const string Reiniciar = "reiniciar";
    public const string SuporteRemoto = "suporte_remoto";
}

public static class PrioridadesAcao
{
    // Prioridades na fila (seção 2.4 da especificação):
    // 1: garantir/redefinir/trocar senha de resgate
    // 2: bloquear/desbloquear
    // 3: instalar/remover
    public const int Resgate = 1;
    public const int Bloqueio = 2;
    public const int Aplicativos = 3;

    public static int ObterPrioridadePadrao(string tipo)
    {
        return tipo switch
        {
            TiposAcao.GarantirResgate or TiposAcao.RedefinirResgate or TiposAcao.TrocarResgate => Resgate,
            TiposAcao.Bloquear or TiposAcao.Desbloquear => Bloqueio,
            TiposAcao.Instalar or TiposAcao.Remover => Aplicativos,
            _ => Bloqueio
        };
    }
}
