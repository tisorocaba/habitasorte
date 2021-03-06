﻿using CsvHelper;
using Excel;
using Habitasorte.Business.Model;
using Habitasorte.Business.Model.Publicacao;
using Habitasorte.Business.Pdf;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlServerCe;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Habitasorte.Business {

    public delegate void SorteioChangedEventHandler(Sorteio s);

    public class SorteioService {

        public event SorteioChangedEventHandler SorteioChanged;

        private Sorteio model;
        public Sorteio Model {
            get { return model; }
            set { model = value; SorteioChanged(model); }
        }

        public SorteioService() {
            Database.Initialize();
        }

        private void Execute(Action<Database> action) {
            using (SqlCeConnection connection = Database.CreateConnection()) {
                using (SqlCeTransaction tx = connection.BeginTransaction()) {
                    Database database = new Database(connection, tx);
                    try {
                        action(database);
                        tx.Commit();
                    } catch {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }
            }
        }

        private void AtualizarStatusSorteio(Database database, string status) {
            Model.StatusSorteio = status;
            database.AtualizarStatusSorteio(status);
        }

        /* Configuração */

        public void ExcluirBancoReiniciarAplicacao() {
            Database.ExcluirBanco();
            System.Windows.Application.Current.Shutdown();
        }

        /* Ações */

        public void AtualizarConfiguracaoPublicacao() {
            Execute(d => {
                d.AtualizarConfiguracaoPublicacao(Model.ConfiguracaoPublicacao);
            });
        }

        public void CarregarSorteio() {
            Execute(d => {
                Model = d.CarregarSorteio();
                Model.ConfiguracaoPublicacao = d.CarregarConfiguracaoPublicacao();
            });
        }

        public void AtualizarSorteio() {
            Execute(d => {
                d.AtualizarSorteio(Model);
                AtualizarStatusSorteio(d, Status.IMPORTACAO);
            });
        }

        public void CarregarListas() {
            Execute(d => {
                Model.Listas = d.CarregarListas();
            });
            
        }

        public void CarregarProximaLista() {
            Execute(d => {
                Model.ProximaLista = d.CarregarProximaLista();
            });
        }

        public int ContagemCandidatos() {
            int contagemCandidatos = 0;
            Execute(d => {
                contagemCandidatos = d.ContagemCandidatos();
            });
            return contagemCandidatos;
        }

        public void AtualizarListas() {
            Execute(d => {
                d.AtualizarListas(Model.Listas);
                AtualizarStatusSorteio(d, Status.SORTEIO);
            });
        }

        public void CriarListasSorteio(string arquivoImportacao, Action<string> updateStatus, Action<int> updateProgress) {
            Execute(d => {
                if (arquivoImportacao.ToLower().EndsWith(".csv")) {
                    using (StreamReader streamReader = File.OpenText(arquivoImportacao)) {
                        using (CsvDataReader csvReader = new CsvDataReader(streamReader)) {
                            CriarListarSorteio(d, csvReader, updateStatus, updateProgress);
                        }
                    }
                } else {
                    using (FileStream fileStream = File.OpenRead(arquivoImportacao)) {
                        using (IExcelDataReader excelReader = CreateExcelReader(fileStream)) {
                            CriarListarSorteio(d, excelReader, updateStatus, updateProgress);
                        }
                    }
                }
                AtualizarStatusSorteio(d, Status.QUANTIDADES);
            });
        }

        private void CriarListarSorteio(Database database, IDataReader dataReader, Action<string> updateStatus, Action<int> updateProgress) {

            List<string> empreendimentos = Model.Empreendimentos
                .OrderBy(e => e.Ordem)
                .Select(e => e.Nome)
                .ToList();

            try {
                database.CriarListasSorteio(empreendimentos, dataReader, updateStatus, updateProgress);
            } catch (Exception exception) {

                string ultimoRegistro;
                try {
                    ultimoRegistro = string.Join("\n", new string[] {
                        "CPF: " + dataReader.GetString(0),
                        "NOME: " + dataReader.GetString(1),
                        "QUANTIDADE_CRITERIOS: " + dataReader.GetString(2),
                        "LISTA_DEFICIENTES: " + dataReader.GetString(3),
                        "LISTA_IDOSOS: " + dataReader.GetString(4),
                        "LISTA_INDICADOS: " + dataReader.GetString(5)
                    });
                } catch {
                    ultimoRegistro = null;
                }

                throw new Exception($"{exception.Message}\n\n- ÚLTIMO REGISTRO LIDO -\n\n{ultimoRegistro}");
            }
        }

        private IExcelDataReader CreateExcelReader(FileStream FileStream) {
            return (FileStream.Name.ToLower().EndsWith(".xlsx")) ?
                ExcelReaderFactory.CreateOpenXmlReader(FileStream) : ExcelReaderFactory.CreateBinaryReader(FileStream);
        }

        public void SortearProximaLista(Action<string> updateStatus, Action<int> updateProgress, Action<string> logText, int? sementePersonalizada = null) {
            Execute(d => {
                d.SortearProximaLista(updateStatus, updateProgress, logText, sementePersonalizada);
                if (Model.StatusSorteio == Status.SORTEIO) {
                    AtualizarStatusSorteio(d, Status.SORTEIO_INICIADO);
                }
                if (d.CarregarProximaLista() == null) {
                    AtualizarStatusSorteio(d, Status.FINALIZADO);
                }
            });
        }

        public string DiretorioExportacaoCSV => Database.DiretorioExportacaoCSV;
        public bool DiretorioExportacaoCSVExistente => Directory.Exists(Database.DiretorioExportacaoCSV);

        public void ExportarListas(Action<string> updateStatus) {
            Execute(d => {
                d.ExportarListas(updateStatus);
            });
        }

        public string PublicarLista(int? idLista, bool teste = false) {

            string url = Model.ConfiguracaoPublicacao.UrlPublicacao;
            string codigo = Model.ConfiguracaoPublicacao.CodigoPublicacao;

            SorteioPub sorteioPublicacao = new SorteioPub {
                Codigo = int.Parse(codigo),
                Nome = Model.Nome
            };

            if (teste) {
                url += "?teste=true";
            } else {
                Execute(d => {
                    sorteioPublicacao.Listas = new List<ListaPub> {
                        d.CarregarListaPublicacao((int) idLista)
                    };
                });
            }

            string data = JsonConvert.SerializeObject(sorteioPublicacao);
            HttpContent responseContent = HttpPost(url, new StringContent(data, Encoding.UTF8, "application/json"), true);

            if (!teste) {
                Execute(d => {
                    d.PublicarLista((int) idLista);
                });
            }

            return responseContent.ReadAsStringAsync().Result;
        }

        public void SalvarLista(Lista lista, string caminhoArquivo) {
            ListaPub listaPublicacao = null;
            Execute(d => { listaPublicacao = d.CarregarListaPublicacao(lista.IdLista); });
            PdfFileWriter.WriteToPdf(caminhoArquivo, Model, listaPublicacao);
            System.Diagnostics.Process.Start(caminhoArquivo);
        }

        private HttpContent HttpPost(string url, HttpContent requestContent, bool jsonContent = false) {

            string usuario = Model.ConfiguracaoPublicacao.UsuarioPublicacao;
            string senha = Model.ConfiguracaoPublicacao.SenhaPublicacao;

            using (HttpClient client = new HttpClient()) {

                string serviceToken = string.Format("{0}:{1}", usuario, senha);
                string encodedServiceToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(serviceToken));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encodedServiceToken);

                if (jsonContent) {
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                }

                HttpResponseMessage response = client.PostAsync(url, requestContent).Result;

                if (response.StatusCode != System.Net.HttpStatusCode.OK) {
                    string responseContent = response.Content.ReadAsStringAsync().Result;
                    throw new Exception($"{(int) response.StatusCode} - {response.ReasonPhrase} \n {responseContent}");
                } else {
                    return response.Content;
                }
            }
        }
    }
}
