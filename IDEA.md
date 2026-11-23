## Visão do projeto

Desenvolver, com auxílio do harness Hermes, um conjunto de ferramentas internas para facilitar o acesso da equipe a serviços e sistemas da empresa. As ferramentas poderão consultar APIs ou executar ações específicas, substituindo tarefas manuais e repetitivas por comandos rápidos.

## Experiência de uso

Cada ferramenta poderá ser executada por um atalho global próprio. Também haverá um atalho central que abrirá uma lista de ferramentas disponíveis, permitindo ao usuário localizar e selecionar a ação desejada.

Os resultados devem poder ser copiados para a área de transferência do Windows e apresentados em janelas pop-up, conforme o comportamento mais adequado para cada ferramenta.

## Configuração e autenticação

As ferramentas poderão ter configurações próprias, incluindo campos de login quando forem necessários.

Ao criar uma ferramenta, o harness deve identificar os dados de configuração e autenticação de que ela precisa e disponibilizar os campos correspondentes na interface de configuração. Sempre que possível, cada ferramenta deve declarar explicitamente esses requisitos, para que o aplicativo central possa apresentá-los de forma consistente.

O sistema poderá oferecer configurações individuais e compartilhadas pela equipe. A forma de armazenar e disponibilizar credenciais deverá ser definida de acordo com as regras internas da empresa.

## Conexão com os serviços

Alguns serviços dependem de VPNs diferentes. Cada ferramenta deve verificar se consegue se comunicar com o serviço de que precisa. Se a comunicação falhar, deve informar:

> Sem conexão com o servidor. Verifique a VPN e tente novamente.

A aplicação não precisa selecionar nem conectar o usuário à VPN; sua responsabilidade é verificar a comunicação e apresentar uma mensagem clara quando não conseguir acessar o serviço.

## Princípios do projeto

Inspirado na filosofia do Microsoft PowerToys, o projeto deve reunir pequenas ferramentas úteis em um único local, com atalhos, descrições, configurações e formas de uso fáceis de entender.

Cada ferramenta deve informar:
- Para que serve.
- Como pode ser executada.
- Quais configurações ou credenciais exige.
- Como apresenta e disponibiliza o resultado.

## Escopo

Este documento descreve a visão do projeto. Os serviços a integrar, as tecnologias, o formato de compartilhamento com a equipe e os detalhes de armazenamento de configurações e credenciais serão definidos posteriormente.
