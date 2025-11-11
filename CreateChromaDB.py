import chromadb
import logging
import requests

# Configure logging
logging.basicConfig(level=logging.INFO)
logging.getLogger('chromadb').setLevel(logging.WARNING)

# Initialize Chroma client to connect to the running server
client = chromadb.HttpClient(host="http://localhost:8000")

# Collection name
collection_name = "collection"

# Embedding API details
embedding_api_url = "https://192.168.118.23/v1/embeddings"
text_model = "bge-m3"

try:
    existing_collections = client.list_collections()
    if not any(col.name == collection_name for col in existing_collections):
        collection = client.create_collection(name=collection_name)
    else:
        collection = client.get_collection(collection_name)
    # Read the content of Options.txt with UTF-8 encoding
    with open("c:\\Users\\nicholas.lee\\Documents\\z\\TKV_Kenji\\Biz.TKV.EmbeddingProcess\\text.txt", "r", encoding="utf-8") as file:
        text = file.read()

    # Chunk the text
    chunk_size = 200
    chunks = [text[i:i + chunk_size] for i in range(0, len(text), chunk_size)]

    # Embed each chunk and add to the collection
    for i, chunk in enumerate(chunks):
        # Prepare the request payload
        payload = {
            "model": text_model,
            "input": chunk
        }

        # Send the request to the embedding API
        response = requests.post(embedding_api_url, json=payload, verify=False)
        if response.status_code != 200:
            logging.error(f"Error embedding chunk {i}: {response.status_code} {response.text}")
            continue

        # Parse the response
        embedding_data = response.json()
        if not embedding_data or "data" not in embedding_data or not embedding_data["data"]:
            logging.error(f"Invalid response for chunk {i}: {response.text}")
            continue

        embedding = embedding_data["data"][0]["embedding"]

        # Add the embedding to the collection
        collection.add(
            ids=[f"chunk_{i}"],
            embeddings=[embedding],
            metadatas=[{"chunk": chunk}]
        )
        logging.info(f"Chunk {i} added to collection.")

except Exception as e:
    logging.error(f"Error: {e}")